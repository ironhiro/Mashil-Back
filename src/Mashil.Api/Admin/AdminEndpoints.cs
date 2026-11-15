using System.Security.Claims;
using System.Text.Json;
using Mashil.Api.Auth;
using Mashil.Api.Endpoints;
using Mashil.Domain;
using Mashil.Import;
using Mashil.Import.Admin;
using Mashil.Import.LocalData;
using Mashil.Import.Normalization;
using Mashil.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Api.Admin;

/// <summary>
/// 관리자 데이터 운영 (ADM-03~06): 임포트 이력·리포트, 수동 임포트·재정규화, 대량 삭제 승인, 정규화 이슈 큐, 매핑 사전,
/// 화장실 직접 수정, 점검 모드. 전부 <c>Admin</c> 역할 필요. 변경 요청(ADM-01)과 신고 큐(ADM-02)는 기존 파일에 있다.
/// </summary>
public static class AdminEndpoints
{
    private const string Src = DataSourceCodes.LocalDataPublicRestroom;

    /// <summary>업로드 파일 최대 크기. 전국 파일은 약 15 MB.</summary>
    public const long MaxUploadBytes = 64L * 1024 * 1024;

    public static IServiceCollection AddAdminServices(this IServiceCollection services, IConfiguration config)
    {
        services.AddSingleton(config.GetSection("Import:Policy").Get<ImportPolicy>() ?? ImportPolicy.Default);
        services.AddSingleton(config.GetSection("Import:LocalData").Get<LocalDataFetchOptions>() ?? new LocalDataFetchOptions());
        services.AddSingleton<AdminJobQueue>();
        services.AddHostedService<AdminJobRunner>();
        return services;
    }

    public static void MapAdminEndpoints(this IEndpointRouteBuilder app)
    {
        var admin = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization("Admin");
        MapImports(admin);
        MapIssues(admin);
        MapToilets(admin);
        MapAppStatus(admin);
    }

    private static IResult Error(string code, int status, string? message = null) =>
        Results.Json(new { error = code, message }, statusCode: status);

    /// <summary>임포트 락이 지금 비어 있는지 잠깐 잡아 보고 놓는다. 실제 배타는 작업이 다시 잡는 락이 맡는다.</summary>
    private static async Task<bool> LockFreeAsync(MashilDbContext db, CancellationToken ct)
    {
        var sourceId = await db.DataSources.Where(s => s.Code == Src).Select(s => s.Id).SingleAsync(ct);
        await using var probe = await ImportLock.TryAcquireAsync(db, sourceId, ct);
        return probe is not null;
    }

    private static async Task<IResult> EnqueueExclusiveAsync(AdminJobQueue queue, MashilDbContext db, AdminJob job, CancellationToken ct)
    {
        if (!await LockFreeAsync(db, ct))
            return Error("import_busy", 409, "다른 임포트나 재정규화가 실행 중입니다 (일일 갱신 포함). 끝난 뒤 다시 시도하세요.");
        if (!queue.TryEnqueue(job, exclusive: true, out var blocking))
            return Error("import_busy", 409, $"관리자 작업이 이미 대기·실행 중입니다: {blocking!.Label}");
        return Results.Accepted($"/api/admin/jobs/{job.Id}", job.View());
    }

    // ------------------------------------------------------------------ 임포트 (ADM-05, DATA-15·16·19·23)

    private static void MapImports(RouteGroupBuilder admin)
    {
        admin.MapGet("/import-runs", async (int? limit, MashilDbContext db, CancellationToken ct) =>
                Results.Ok(await db.ImportRuns.AsNoTracking().OrderByDescending(r => r.Id).Take(Math.Clamp(limit ?? 50, 1, 200)).ToListAsync(ct)))
            .WithSummary("임포트 실행 이력 (관리자)")
            .WithDescription("최신순. 행마다 상태, 전체·신규·변경·동일·건너뜀, 누락·복귀·삭제(대상), 새 이슈 수(`newIssues`), 메시지.")
            .Produces<IReadOnlyList<ImportRun>>();

        admin.MapGet("/import-runs/{id:long}", async (long id, MashilDbContext db, CancellationToken ct) =>
                await AdminReports.RunReportAsync(db, id, ct) is { } report ? Results.Ok(report) : Results.NotFound())
            .WithSummary("임포트 실행 리포트 (관리자)")
            .WithDescription("`run` + `newIssuesByType` (그 실행 동안 새로 생긴 정규화 이슈를 필드·유형·지금 상태별로).")
            .Produces<ImportRunReport>();

        admin.MapPost("/import-runs/upload", async (HttpRequest req, ClaimsPrincipal principal, AdminJobQueue queue, MashilDbContext db, CancellationToken ct) =>
            {
                if (!req.HasFormContentType) return Error("file_required", 400, "multipart/form-data 의 file 필드가 필요합니다");
                var form = await req.ReadFormAsync(ct);
                var upload = form.Files.GetFile("file");
                if (upload is null || upload.Length == 0) return Error("file_required", 400, "파일이 비어 있습니다");
                if (upload.Length > MaxUploadBytes) return Error("file_too_large", 413);

                using var buffer = new MemoryStream((int)upload.Length);
                await upload.CopyToAsync(buffer, ct);
                var file = new SourceFile(buffer.ToArray(), Path.GetFileName(upload.FileName));
                var options = new ImportOptions { Force = IsOn(form["force"]), SkipValidation = IsOn(form["skipValidation"]) };

                var job = new AdminJob(AdminJobKind.Upload, $"{file.FileName} ({file.Bytes.LongLength:N0} bytes)", principal.TryGetUserId(),
                    (sp, token) => new ImportRunner(sp.GetRequiredService<MashilDbContext>(), sp.GetRequiredService<ImportPolicy>())
                        .RunAsync(Src, file, ImportTrigger.Upload, options, ct: token)!);
                return await EnqueueExclusiveAsync(queue, db, job, ct);
            })
            .WithMetadata(new RequestSizeLimitAttribute(MaxUploadBytes + 1024 * 1024))
            .WithSummary("파일 업로드로 임포트 (관리자, DATA-19)")
            .WithDescription("""
                `multipart/form-data`: `file` (LOCALDATA 전국 CSV, CP949·UTF-8 자동 판별), 선택 `force=true` (직전 성공과 같은 파일이어도 처리),
                `skipValidation=true` (크기·행 수 비교 검증 생략 — 원본 형식이 정상적으로 크게 바뀐 경우). 일일 갱신과 같은 파이프라인.
                202 + 작업(`/api/admin/jobs/{id}`). 다른 임포트가 실행 중이면 409 `import_busy`.
                """)
            .Produces<AdminJobView>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status409Conflict);

        admin.MapPost("/import-runs/fetch", async (ImportStartInput? input, ClaimsPrincipal principal, AdminJobQueue queue, MashilDbContext db, CancellationToken ct) =>
            {
                var options = new ImportOptions { Force = input?.Force ?? false, SkipValidation = input?.SkipValidation ?? false };
                var job = new AdminJob(AdminJobKind.Fetch, "LOCALDATA 내려받아 임포트", principal.TryGetUserId(), async (sp, token) =>
                {
                    var jobDb = sp.GetRequiredService<MashilDbContext>();
                    var source = await jobDb.DataSources.AsNoTracking().SingleAsync(s => s.Code == Src, token);
                    var file = await new LocalDataFileFetcher(sp.GetRequiredService<LocalDataFetchOptions>()).FetchAsync(source, token);
                    return await new ImportRunner(jobDb, sp.GetRequiredService<ImportPolicy>()).RunAsync(Src, file, ImportTrigger.Manual, options, ct: token);
                });
                return await EnqueueExclusiveAsync(queue, db, job, ct);
            })
            .WithSummary("지금 내려받아 임포트 (관리자)")
            .WithDescription("일일 갱신을 지금 한 번 실행한다 (다운로드 → 같은 파이프라인). body 선택: `{ force, skipValidation }`. 다운로드가 막히면 작업이 Failed — 파일 업로드를 쓴다.")
            .Produces<AdminJobView>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status409Conflict);

        admin.MapPost("/import-runs/renormalize", async (RenormalizeInput? input, ClaimsPrincipal principal, AdminJobQueue queue, MashilDbContext db, CancellationToken ct) =>
            {
                var onlyOutdated = input?.OnlyOutdated ?? false;
                var job = new AdminJob(AdminJobKind.Renormalize, onlyOutdated ? "재정규화 (바뀐 것만)" : "재정규화 (전체)", principal.TryGetUserId(),
                    (sp, token) => new ImportRunner(sp.GetRequiredService<MashilDbContext>(), sp.GetRequiredService<ImportPolicy>())
                        .RenormalizeAsync(Src, onlyOutdated, ct: token)!);
                return await EnqueueExclusiveAsync(queue, db, job, ct);
            })
            .WithSummary("재정규화 실행 (관리자, DATA-14)")
            .WithDescription("저장된 원본으로 다시 정규화. `onlyOutdated=true` 면 정규화 버전이 지금과 다른 화장실만 (매핑 사전 변경분 포함). 202 + 작업.")
            .Produces<AdminJobView>(StatusCodes.Status202Accepted)
            .Produces(StatusCodes.Status409Conflict);

        admin.MapPost("/import-runs/{id:long}/approve-removals", (long id, MashilDbContext db, ImportPolicy policy, CancellationToken ct) =>
                UnderLockAsync(db, id, ct, () => MissingRecordProcessor.ApprovePendingRemovalsAsync(db, id, policy, ct)))
            .WithSummary("보류된 대량 삭제 승인 (관리자, DATA-15)")
            .WithDescription("PendingApproval 실행의 삭제 대상을 Removed 로. 응답 `{ count }`. 그 뒤 새 실행이 있었거나 승인 대기가 아니면 409.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status409Conflict);

        admin.MapPost("/import-runs/{id:long}/reject-removals", (long id, MashilDbContext db, ImportPolicy policy, CancellationToken ct) =>
                UnderLockAsync(db, id, ct, () => MissingRecordProcessor.RejectPendingRemovalsAsync(db, id, policy, ct)))
            .WithSummary("보류된 대량 삭제 거부 (관리자)")
            .WithDescription("삭제하지 않는다. 대상의 연속 누락 횟수를 0으로 되돌린다 (Missing 표시는 유지, 다시 나타나면 Active). 응답 `{ count }` = 되돌린 원본 행 수.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status409Conflict);

        admin.MapGet("/jobs", (AdminJobQueue queue) => Results.Ok(queue.Recent()))
            .WithSummary("관리자 작업 목록 (이 API 인스턴스)")
            .WithDescription("최근 50건. 상태 `Queued` / `Running` / `Succeeded` / `Failed` / `Busy`(임포트 락이 잡혀 있음). 결과 실행은 `runId`.")
            .Produces<IReadOnlyList<AdminJobView>>();

        admin.MapGet("/jobs/{id:guid}", (Guid id, AdminJobQueue queue) => queue.Find(id) is { } job ? Results.Ok(job.View()) : Results.NotFound())
            .WithSummary("관리자 작업 하나")
            .Produces<AdminJobView>();

        admin.MapGet("/normalization/report", async (MashilDbContext db, CancellationToken ct) =>
                Results.Ok(await AdminReports.NormalizationReportAsync(db, DateTime.UtcNow, ct)))
            .WithSummary("정규화 리포트 (관리자, DATA-16)")
            .WithDescription("Removed 를 뺀 화장실 기준 필드별 성공률(개방시간, 구분, 전화번호, 좌표)과 Open 이슈의 상위 원본 값 50개.")
            .Produces<NormalizationReport>();
    }

    private static bool IsOn(Microsoft.Extensions.Primitives.StringValues v) =>
        v.Count > 0 && (v[0] is "true" or "1" or "on");

    private static async Task<IResult> UnderLockAsync(MashilDbContext db, long runId, CancellationToken ct, Func<Task<int>> action)
    {
        if (!await db.ImportRuns.AnyAsync(r => r.Id == runId, ct)) return Results.NotFound();
        var sourceId = await db.DataSources.Where(s => s.Code == Src).Select(s => s.Id).SingleAsync(ct);
        await using var importLock = await ImportLock.TryAcquireAsync(db, sourceId, ct);
        if (importLock is null) return Error("import_busy", 409, "다른 임포트가 실행 중입니다. 끝난 뒤 다시 시도하세요.");
        try
        {
            return Results.Ok(new { count = await action() });
        }
        catch (InvalidOperationException ex)
        {
            return Error("invalid_state", 409, ex.Message);
        }
    }

    public sealed record ImportStartInput(bool? Force, bool? SkipValidation);
    public sealed record RenormalizeInput(bool? OnlyOutdated);

    // ------------------------------------------------------------------ 정규화 이슈·매핑 사전 (ADM-03, DATA-12·13)

    private static void MapIssues(RouteGroupBuilder admin)
    {
        admin.MapGet("/issues", async (string? field, IssueType? type, IssueStatus? status, string? rawValue, long? toiletId, int? offset, int? limit,
                MashilDbContext db, CancellationToken ct) =>
                Results.Ok(await IssueQueue.ListAsync(db, new IssueFilter(field, type, status, rawValue, toiletId),
                    Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 50, 1, 200), ct)))
            .WithSummary("정규화 이슈 목록 (관리자)")
            .WithDescription("필터: `field`(opening_hours, kind, phone, facilities, data_reference_date, geocode), `type`, `status`(Open/Resolved/Ignored), `rawValue`, `toiletId`. 최신순 `{ total, items }`.")
            .Produces<Paged<IssueRow>>();

        admin.MapGet("/issues/groups", async (string? field, IssueType? type, IssueStatus? status, int? offset, int? limit, MashilDbContext db, CancellationToken ct) =>
                Results.Ok(await IssueQueue.GroupsAsync(db, new IssueFilter(field, type, status ?? IssueStatus.Open),
                    Math.Max(0, offset ?? 0), Math.Clamp(limit ?? 50, 1, 200), ct)))
            .WithSummary("같은 원본 값 묶음 (관리자)")
            .WithDescription("(필드, 유형, 원본 값)으로 묶어 건수 많은 순. status 기본 `Open`. 묶음 하나를 일괄 처리하거나 매핑 사전에 등록한다.")
            .Produces<Paged<IssueGroup>>();

        admin.MapGet("/issues/counts", async (MashilDbContext db, CancellationToken ct) => Results.Ok(await IssueQueue.CountsAsync(db, ct)))
            .WithSummary("이슈 건수 (필드·유형·상태별)")
            .Produces<IReadOnlyList<IssueTypeCount>>();

        admin.MapPost("/issues/bulk", async (IssueBulkInput input, ClaimsPrincipal principal, MashilDbContext db, CancellationToken ct) =>
            {
                try
                {
                    var filter = input.FieldName is null && input.Type is null && input.RawValue is null && input.ToiletId is null
                        ? null
                        : new IssueFilter(input.FieldName, input.Type, input.FromStatus ?? IssueStatus.Open, input.RawValue, input.ToiletId,
                            NullRawValue: input.RawValue is null && input.ToiletId is null); // 원본 값이 빈 묶음 (전체가 아니라)
                    var n = await IssueQueue.SetStatusAsync(db, input.Ids, filter, input.Status, principal.TryGetUserId(), DateTime.UtcNow, ct);
                    return Results.Ok(new { count = n });
                }
                catch (ArgumentException ex)
                {
                    return Error("invalid_request", 400, ex.Message);
                }
            })
            .WithSummary("이슈 일괄 처리 (관리자)")
            .WithDescription("""
                `status` = `Resolved` / `Ignored` / `Open`(되돌리기). 대상은 `ids` 또는 묶음 필터(`fieldName`, `type`, `rawValue`, 선택 `toiletId`,
                `fromStatus` 기본 Open). 해결·무시한 이슈는 재임포트·재정규화가 다시 만들지 않는다. 응답 `{ count }`.
                묶음 필터에서 `rawValue` 가 없으면(null) 원본 값이 빈 묶음을 뜻한다 (필드·유형 전체를 한 번에 바꾸지 않게).
                """)
            .Produces(StatusCodes.Status200OK);

        admin.MapGet("/value-mappings", async (string? field, bool? includeInactive, MashilDbContext db, CancellationToken ct) =>
                Results.Ok(await ValueMappingService.ListAsync(db, field, includeInactive ?? false, ct)))
            .WithSummary("매핑 사전 (관리자)")
            .Produces<IReadOnlyList<ValueMappingView>>();

        admin.MapPost("/value-mappings", async (ValueMappingInput input, ClaimsPrincipal principal, AdminJobQueue queue, MashilDbContext db, CancellationToken ct) =>
            {
                string canonical;
                try
                {
                    canonical = input.CanonicalJson
                        ?? (input.Hours is { } h ? new HoursOutcome(h.Status, h.Entries ?? []).ToJson() : throw new FormatException("hours 또는 canonicalJson 이 필요합니다"));
                    var change = await ValueMappingService.CreateAsync(db, input.FieldName, input.RawKey?.Trim() ?? "", canonical, principal.TryGetUserId(), DateTime.UtcNow, ct);
                    return Results.Ok(MappingResponse(change, EnqueueMappingRenormalize(queue, change, principal.TryGetUserId())));
                }
                catch (FormatException ex)
                {
                    return Error("invalid_mapping", 400, ex.Message);
                }
            })
            .WithSummary("매핑 사전 등록 (관리자, DATA-13)")
            .WithDescription("""
                `{ fieldName: "opening_hours", rawKey, hours: { status, entries: [{ day, open, close, closed }] } }` (또는 `canonicalJson`).
                rawKey 는 개방시간 이슈의 원본 값(`개방시간|개방시간상세`, 공백 정리). status = AlwaysOpen / Scheduled / NotOpen / Irregular / FollowsFacility / Unknown,
                entries 는 Scheduled 일 때 요일(Mon~Sun, Holiday)별 분 단위 시간(0~1440) 또는 `closed: true`.
                같은 키의 이전 매핑은 꺼지고, 같은 원본 값의 Open 이슈는 Resolved. 그 원본 값을 가진 화장실은 백그라운드로 다시 정규화한다
                (응답의 `job`, 대상 수 `affectedToilets`). 지금 매핑을 읽는 필드는 개방시간뿐이다.
                """)
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status400BadRequest);

        admin.MapDelete("/value-mappings/{id:long}", async (long id, ClaimsPrincipal principal, AdminJobQueue queue, MashilDbContext db, CancellationToken ct) =>
                await ValueMappingService.DeactivateAsync(db, id, ct) is { } change
                    ? Results.Ok(MappingResponse(change, EnqueueMappingRenormalize(queue, change, principal.TryGetUserId())))
                    : Results.NotFound())
            .WithSummary("매핑 해제 (관리자)")
            .WithDescription("매핑을 끄고, 이 매핑으로 해결된 이슈를 Open 으로 되돌린 뒤 대상 화장실을 다시 정규화한다.")
            .Produces(StatusCodes.Status200OK)
            .Produces(StatusCodes.Status404NotFound);
    }

    private static object MappingResponse(MappingChange change, AdminJob? job) => new
    {
        mapping = change.Mapping,
        issuesChanged = change.IssuesChanged,
        affectedToilets = change.AffectedToiletIds.Count,
        job = job?.View(),
    };

    private static AdminJob? EnqueueMappingRenormalize(AdminJobQueue queue, MappingChange change, Guid? adminId)
    {
        if (change.AffectedToiletIds.Count == 0) return null;
        var ids = change.AffectedToiletIds;
        var job = new AdminJob(AdminJobKind.MappingRenormalize, $"매핑 #{change.Mapping.Id} 반영 ({ids.Count:N0}곳)", adminId,
            (sp, token) => new ImportRunner(sp.GetRequiredService<MashilDbContext>(), sp.GetRequiredService<ImportPolicy>())
                .RenormalizeAsync(Src, ct: token, toiletIds: ids, label: $"(mapping #{change.Mapping.Id})")!);
        queue.TryEnqueue(job, exclusive: false, out _);
        return job;
    }

    public sealed record IssueBulkInput(IssueStatus Status, IReadOnlyList<long>? Ids, string? FieldName, IssueType? Type, string? RawValue, long? ToiletId, IssueStatus? FromStatus);
    public sealed record HoursInput(ScheduleStatus Status, List<ScheduleEntry>? Entries);
    public sealed record ValueMappingInput(string FieldName, string? RawKey, HoursInput? Hours, string? CanonicalJson);

    // ------------------------------------------------------------------ 화장실 직접 수정 (ADM-04)

    private static void MapToilets(RouteGroupBuilder admin)
    {
        admin.MapGet("/toilets/{id:long}", async (long id, MashilDbContext db, CancellationToken ct) =>
                await ToiletAdminEditor.ViewAsync(db, id, ct) is { } view ? Results.Ok(view) : Results.NotFound())
            .WithSummary("화장실 편집 화면 (관리자)")
            .WithDescription("지금 값(`current`), 원본 값(`source`, 제보로 추가된 곳은 null), 오버라이드 이력(꺼진 것 포함, 최신순), 이 화장실에 온 변경 요청, Open 이슈 수.")
            .Produces<ToiletAdminView>();

        admin.MapPost("/toilets/{id:long}/overrides", async (long id, JsonElement body, ClaimsPrincipal principal, MashilDbContext db, CancellationToken ct) =>
            {
                try
                {
                    return await ToiletAdminEditor.ApplyAsync(db, id, body, principal.GetUserIdOrThrow(), DateTime.UtcNow, ct) is { } r
                        ? Results.Ok(r)
                        : Results.NotFound();
                }
                catch (FormatException ex)
                {
                    return Error("invalid_request", 400, ex.Message);
                }
            })
            .WithSummary("화장실 직접 수정 (관리자)")
            .WithDescription("""
                바꿀 필드만 보낸다: `name`, `roadAddress`, `lotAddress`, `phone`, `openHours`, `openHoursDetail`, `latitude`+`longitude`, `status`(`Active` / `Removed`).
                값마다 관리자 오버라이드(우선순위 최고)를 만들고 원본부터 다시 계산한다. 값을 `null` 로 보내면 그 필드의 관리자 오버라이드를 끈다 (원본으로 되돌리기,
                좌표는 둘 다 null). 응답 `{ toiletId, appliedFields, clearedFields, ignoredKeys }`.
                """)
            .Produces<AdminEditResult>()
            .Produces(StatusCodes.Status404NotFound);

        admin.MapDelete("/toilets/{id:long}/overrides/{overrideId:long}", async (long id, long overrideId, MashilDbContext db, CancellationToken ct) =>
                await ToiletAdminEditor.DeactivateOverrideAsync(db, id, overrideId, DateTime.UtcNow, ct) ? Results.NoContent() : Results.NotFound())
            .WithSummary("오버라이드 끄기 (관리자)")
            .WithDescription("출처와 상관없이(관리자, 변경 요청, 지오코딩) 활성 오버라이드 하나를 끄고 원본부터 다시 계산한다. 이력에는 꺼진 채 남는다.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
    }

    // ------------------------------------------------------------------ 점검 모드 (ADM-06, OPS-05)

    private static void MapAppStatus(RouteGroupBuilder admin)
    {
        admin.MapGet("/app-status", async (AppStatusProvider status, MashilDbContext db, CancellationToken ct) =>
            {
                var row = await AppStatusStore.LoadAsync(db, ct);
                return Results.Ok(new AppStatusAdminView(await status.GetAsync(ct), row, status.ConfigValue));
            })
            .WithSummary("점검 모드 설정 (관리자)")
            .WithDescription("`effective`(지금 적용 중), `stored`(DB 행, 없으면 null), `config`(설정 App:Status — 여기서 켠 플래그는 DB 와 상관없이 켜짐).")
            .Produces<AppStatusAdminView>();

        admin.MapPut("/app-status", async (AppStatusInput input, ClaimsPrincipal principal, AppStatusProvider status, MashilDbContext db, CancellationToken ct) =>
            {
                var row = await AppStatusStore.SaveAsync(db, input.Maintenance, input.ReadOnly, input.Message, input.Until,
                    principal.TryGetUserId(), DateTime.UtcNow, ct);
                status.Invalidate();
                return Results.Ok(new AppStatusAdminView(await status.GetAsync(ct), row, status.ConfigValue));
            })
            .WithSummary("점검 모드 켜기·끄기 (관리자)")
            .WithDescription("`{ maintenance, readOnly, message, until }`. 이 인스턴스는 바로, 다른 인스턴스는 캐시(기본 5초) 뒤 반영. 점검 중에도 관리자와 `/api/admin`·`/api/auth` 쓰기는 막지 않는다.")
            .Produces<AppStatusAdminView>();
    }

    public sealed record AppStatusInput(bool Maintenance, bool ReadOnly, string? Message, DateTimeOffset? Until);
    public sealed record AppStatusAdminView(AppStatusSnapshot Effective, AppStatusSetting? Stored, AppStatusSnapshot Config);
}
