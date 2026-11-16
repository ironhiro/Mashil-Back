using System.Security.Claims;
using Mashil.Api.Auth;
using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Mashil.Api.Endpoints;

/// <summary>
/// 평점 / 댓글 / 변경 요청. 사용자 ID 는 JWT sub claim 에서 추출.
/// 관리자 엔드포인트는 RequireRole("Admin") 으로 게이트.
/// </summary>
public static class InteractionEndpoints
{
    public static void MapInteractionEndpoints(this IEndpointRouteBuilder app)
    {
        var ratings = app.MapGroup("/api/toilets/{toiletId:long}/ratings").WithTags("Ratings");

        ratings.MapPost("", async (
            long toiletId,
            RatingInput input,
            ClaimsPrincipal principal,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.GetUserIdOrThrow();
            if (input.Score is < 1 or > 5)
                return Results.BadRequest(new { error = "score must be 1..5" });
            if (!await db.Toilets.Where(ToiletEndpoints.IsVisible).AnyAsync(t => t.Id == toiletId, ct)) return Results.NotFound(); // 삭제된 곳은 받지 않음

            // 같은 사람이 동시에 두 번 보내면 둘 다 "없음"을 보고 INSERT 해 유니크 위반이 난다 → 한 번 다시 읽어 갱신
            for (var attempt = 0; ; attempt++)
            {
                var existing = await db.Ratings.FirstOrDefaultAsync(r => r.UserId == userId && r.ToiletId == toiletId, ct);
                if (existing is null)
                {
                    db.Ratings.Add(new Rating { UserId = userId, ToiletId = toiletId, Score = input.Score });
                }
                else
                {
                    existing.Score = input.Score;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                try
                {
                    await db.SaveChangesAsync(ct);
                    return Results.NoContent();
                }
                catch (DbUpdateException ex) when (attempt == 0 && IsUniqueViolation(ex))
                {
                    db.ChangeTracker.Clear();
                }
            }
        })
        .RequireAuthorization()
        .RequireConsent()
        .WithSummary("화장실 별점 등록/갱신 (인증 필요)")
        .WithDescription("(user, toilet) 유니크 — 같은 사용자가 같은 화장실에 다시 POST 하면 기존 점수 갱신. `score` 1~5 범위 외면 400, 없거나 삭제(Removed)된 화장실이면 404.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        var comments = app.MapGroup("/api/toilets/{toiletId:long}/comments").WithTags("Comments");

        comments.MapGet("", async (long toiletId, ClaimsPrincipal principal, MashilDbContext db, CancellationToken ct) =>
        {
            var viewer = principal.TryGetUserId();
            var q = db.Comments.Where(c => c.ToiletId == toiletId && !c.Hidden);
            if (viewer is { } v)
                q = q.Where(c => !db.UserBlocks.Any(b => b.BlockerId == v && b.BlockedId == c.UserId));
            var rows = await q
                .OrderByDescending(c => c.CreatedAt)
                .Take(100)
                .Join(db.UserProfiles, c => c.UserId, u => u.Id, (c, u) => new { c, u.DisplayName, u.Status })
                .ToListAsync(ct);

            // 도움돼요/아니에요 집계와 내 표
            var ids = rows.Select(r => r.c.Id).ToList();
            var tallies = await db.CommentVotes
                .Where(v => ids.Contains(v.CommentId))
                .GroupBy(v => v.CommentId)
                .Select(g => new { g.Key, Up = g.Count(v => v.Value > 0), Down = g.Count(v => v.Value < 0) })
                .ToDictionaryAsync(x => x.Key, ct);
            var mine = viewer is { } me
                ? await db.CommentVotes.Where(v => v.UserId == me && ids.Contains(v.CommentId))
                    .ToDictionaryAsync(v => v.CommentId, v => (int)v.Value, ct)
                : [];

            return Results.Ok(rows
                .OrderByDescending(r => r.c.CreatedAt)
                .Select(r => new CommentView(r.c.Id, r.c.ToiletId, r.c.UserId,
                    CommentAuthor.Name(r.DisplayName, r.Status), r.Status == UserStatus.Deleted,
                    r.c.Text, r.c.Hidden, r.c.CreatedAt, r.c.UpdatedAt, viewer == r.c.UserId,
                    tallies.TryGetValue(r.c.Id, out var tl) ? tl.Up : 0,
                    tallies.TryGetValue(r.c.Id, out var td) ? td.Down : 0,
                    mine.TryGetValue(r.c.Id, out var mv) ? mv : 0)));
        })
        .WithSummary("화장실 댓글 목록 (공개)")
        .WithDescription("숨김 댓글 제외, 최신순 100건. 토큰을 보내면 내가 차단한 사용자의 댓글도 빠진다 (UGC-02). 탈퇴한 작성자는 `authorName = \"탈퇴한 사용자\"`. `upCount`/`downCount` 는 도움돼요/아니에요 수, `myVote` 는 내 표 (1, -1, 0 = 없음; 토큰을 보냈을 때만).")
        .Produces<IReadOnlyList<CommentView>>();

        comments.MapPost("", async (
            long toiletId,
            CommentInput input,
            ClaimsPrincipal principal,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.GetUserIdOrThrow();
            if (string.IsNullOrWhiteSpace(input.Text))
                return Results.BadRequest(new { error = "text required" });
            if (input.Text.Trim().Length > MaxCommentLength)
                return Results.BadRequest(new { error = $"text too long (max {MaxCommentLength})" });
            if (!await db.Toilets.Where(ToiletEndpoints.IsVisible).AnyAsync(t => t.Id == toiletId, ct)) return Results.NotFound(); // 삭제된 곳은 받지 않음

            var c = new Comment { UserId = userId, ToiletId = toiletId, Text = input.Text.Trim() };
            db.Comments.Add(c);
            await db.SaveChangesAsync(ct);
            // 엔티티를 그대로 내보내지 않고 목록과 같은 모양으로
            var author = await db.UserProfiles.Where(u => u.Id == userId)
                .Select(u => new { u.DisplayName, u.Status }).FirstOrDefaultAsync(ct);
            return Results.Created($"/api/toilets/{toiletId}/comments/{c.Id}", new CommentView(
                c.Id, c.ToiletId, c.UserId,
                CommentAuthor.Name(author?.DisplayName, author?.Status ?? UserStatus.Active), false,
                c.Text, c.Hidden, c.CreatedAt, c.UpdatedAt, true, 0, 0, 0));
        })
        .RequireAuthorization()
        .RequireConsent()
        .WithSummary("댓글 작성 (인증 필요)")
        .WithDescription("빈 문자열/공백만이면 400, 없거나 삭제(Removed)된 화장실이면 404. 작성자 ID 는 JWT 에서 추출 — 페이로드로 받지 않음. 응답은 목록과 같은 `CommentView`.")
        .Produces<CommentView>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        app.MapPut("/api/comments/{commentId:long}/vote", async (
            long commentId,
            VoteInput input,
            ClaimsPrincipal principal,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.GetUserIdOrThrow();
            if (input.Value is < -1 or > 1)
                return Results.BadRequest(new { error = "value must be 1, -1 or 0" });
            var comment = await db.Comments.Where(c => c.Id == commentId && !c.Hidden)
                .Select(c => new { c.UserId }).FirstOrDefaultAsync(ct);
            if (comment is null) return Results.NotFound();
            if (comment.UserId == userId)
                return Results.BadRequest(new { error = "cannot vote on your own comment" });

            // 같은 사람의 표가 동시에 두 번 오면 둘 다 INSERT 해 PK 위반 → 한 번 다시 읽어 갱신
            for (var attempt = 0; ; attempt++)
            {
                var existing = await db.CommentVotes.FirstOrDefaultAsync(v => v.CommentId == commentId && v.UserId == userId, ct);
                if (input.Value == 0)
                {
                    if (existing is not null) db.CommentVotes.Remove(existing);
                }
                else if (existing is null)
                {
                    db.CommentVotes.Add(new CommentVote { CommentId = commentId, UserId = userId, Value = (short)input.Value });
                }
                else
                {
                    existing.Value = (short)input.Value;
                    existing.UpdatedAt = DateTime.UtcNow;
                }
                try
                {
                    await db.SaveChangesAsync(ct);
                    break;
                }
                catch (DbUpdateConcurrencyException) when (attempt == 0)
                {
                    db.ChangeTracker.Clear(); // 지우거나 고치려던 표를 다른 요청이 먼저 지움
                }
                catch (DbUpdateException ex) when (attempt == 0 && IsUniqueViolation(ex))
                {
                    db.ChangeTracker.Clear();
                }
            }

            var up = await db.CommentVotes.CountAsync(v => v.CommentId == commentId && v.Value > 0, ct);
            var down = await db.CommentVotes.CountAsync(v => v.CommentId == commentId && v.Value < 0, ct);
            return Results.Ok(new VoteResult(up, down, input.Value));
        })
        .WithTags("Comments")
        .RequireAuthorization()
        .RequireConsent()
        .WithSummary("댓글 도움돼요 / 아니에요 (인증 필요)")
        .WithDescription("`value` 1 = 도움돼요, -1 = 아니에요, 0 = 취소. 사용자당 댓글 하나에 한 표 (다시 보내면 바뀜). 자기 댓글은 400, 가려졌거나 없는 댓글은 404. 바뀐 집계를 돌려준다.")
        .Produces<VoteResult>()
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status404NotFound);

        var changes = app.MapGroup("/api/change-requests").WithTags("ChangeRequests");

        changes.MapPost("", async (
            ChangeRequestInput input,
            ClaimsPrincipal principal,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            var requesterId = principal.GetUserIdOrThrow();
            var cr = new ChangeRequest
            {
                RequesterId = requesterId,
                TargetToiletId = input.TargetToiletId,
                Kind = input.Kind,
                PayloadJson = input.PayloadJson ?? "{}",
            };
            db.ChangeRequests.Add(cr);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/change-requests/{cr.Id}", cr);
        })
        .RequireAuthorization()
        .RequireConsent()
        .WithSummary("변경 요청 제출 (인증 필요)")
        .WithDescription("화장실 추가/수정/삭제 요청을 큐에 적재. 즉시 반영 안 됨 — 관리자 승인 후 별도 서비스가 적용. 상태는 항상 `Pending` 으로 시작.")
        .Produces<ChangeRequest>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized);

        // Admin endpoints
        var admin = app.MapGroup("/api/admin/change-requests").WithTags("Admin").RequireAuthorization("Admin");

        admin.MapGet("", async (ChangeRequestStatus? status, MashilDbContext db, CancellationToken ct) =>
        {
            var query = db.ChangeRequests.AsQueryable();
            if (status is not null) query = query.Where(c => c.Status == status);
            var list = await query.OrderByDescending(c => c.CreatedAt).Take(200).ToListAsync(ct);
            // 승인한 요청이 실제로 남긴 것 (ADM-01): 오버라이드 필드 / 새 화장실 / Removed 표시
            var outcomes = await Mashil.Import.Admin.AdminReports.ChangeRequestOutcomesAsync(
                db, list.Where(c => c.Status == ChangeRequestStatus.Approved).Select(c => c.Id).ToList(), ct);
            return Results.Ok(list.Select(c => new AdminChangeRequestRow(
                c.Id, c.RequesterId, c.TargetToiletId, c.Kind, c.PayloadJson, c.Status, c.ReviewedById, c.ReviewedAt, c.ReviewNote, c.CreatedAt,
                outcomes.GetValueOrDefault(c.Id))));
        })
        .WithSummary("변경 요청 큐 조회 (관리자)")
        .WithDescription("`status` 필터 (`Pending` / `Approved` / `Rejected`) — 생략 시 전체. 최신순 200건. `Admin` 역할 필요. 승인된 요청은 `applied` 에 반영 결과 (`toiletId`, 새 화장실이면 `createdToilet`, 삭제면 `markedRemoved`, 오버라이드 `fields` 와 지금도 쓰이는 `activeFields`).")
        .Produces<IReadOnlyList<AdminChangeRequestRow>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        admin.MapPatch("/{id:long}", async (
            long id,
            ChangeRequestReview review,
            ClaimsPrincipal principal,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var cr = await db.ChangeRequests
                .FromSqlInterpolated($"SELECT * FROM change_requests WHERE \"Id\" = {id} FOR UPDATE")
                .SingleOrDefaultAsync(ct);
            if (cr is null) return Results.NotFound();
            if (cr.Status != ChangeRequestStatus.Pending)
                return Results.Conflict(new { message = $"이미 처리된 요청입니다 ({cr.Status})" });

            var adminId = principal.GetUserIdOrThrow();
            var now = DateTime.UtcNow;
            Mashil.Import.ChangeRequestApplier.Result? applied = null;
            if (review.Approve)
            {
                try
                {
                    applied = await Mashil.Import.ChangeRequestApplier.ApplyAsync(db, cr, adminId, now, ct);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Text.Json.JsonException)
                {
                    return Results.UnprocessableEntity(new { message = "요청 내용을 반영할 수 없습니다: " + ex.Message });
                }
            }

            cr.Status = review.Approve ? ChangeRequestStatus.Approved : ChangeRequestStatus.Rejected;
            cr.ReviewedAt = now;
            cr.ReviewedById = adminId;
            cr.ReviewNote = review.Note;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return applied is null ? Results.NoContent() : Results.Ok(applied);
        })
        .WithSummary("변경 요청 승인/반려 (관리자)")
        .WithDescription("""
            `approve = true` → 같은 트랜잭션에서 화장실에 반영하고 `Approved` (반영 결과 200), `false` → `Rejected` (204).
            반영은 원본을 직접 고치지 않고 오버라이드(source = ChangeRequest)로 남기므로 재임포트해도 유지된다.
            이미 처리된 요청은 409. 반영할 수 없는 내용(대상 없음, 이름 없는 추가 요청)은 422.
            """)
        .Produces<Mashil.Import.ChangeRequestApplier.Result>()
        .Produces(StatusCodes.Status409Conflict)
        .Produces(StatusCodes.Status422UnprocessableEntity)
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);
    }

    public const int MaxCommentLength = 1000;

    /// <summary>관리자 변경 요청 큐의 행. <see cref="ChangeRequest"/> 와 같은 필드 + 승인 반영 결과.</summary>
    public sealed record AdminChangeRequestRow(
        long Id, Guid RequesterId, long? TargetToiletId, ChangeRequestKind Kind, string PayloadJson, ChangeRequestStatus Status,
        Guid? ReviewedById, DateTime? ReviewedAt, string? ReviewNote, DateTime CreatedAt,
        Mashil.Import.Admin.ChangeRequestOutcome? Applied);

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>댓글 목록 행. `authorDeleted` 면 작성자가 탈퇴함. `isMine` 은 토큰을 보냈을 때만 의미 있음.</summary>
    public sealed record CommentView(
        long Id, long ToiletId, Guid UserId, string AuthorName, bool AuthorDeleted,
        string Text, bool Hidden, DateTime CreatedAt, DateTime UpdatedAt, bool IsMine,
        int UpCount, int DownCount, int MyVote);

    /// <summary>댓글 평가 페이로드.</summary>
    /// <param name="Value">1 도움돼요 / -1 아니에요 / 0 취소.</param>
    public sealed record VoteInput(int Value);

    /// <summary>평가 뒤 집계.</summary>
    public sealed record VoteResult(int UpCount, int DownCount, int MyVote);

    /// <summary>별점 페이로드.</summary>
    /// <param name="Score">1~5 정수.</param>
    public sealed record RatingInput(int Score);

    /// <summary>댓글 페이로드.</summary>
    /// <param name="Text">본문 (앞뒤 공백 trim 후 저장; 빈 값 거부).</param>
    public sealed record CommentInput(string Text);

    /// <summary>변경 요청 페이로드.</summary>
    /// <param name="Kind">요청 종류 (`Add` / `Update` / `Delete`).</param>
    /// <param name="TargetToiletId">대상 toilet id — `Add` 의 경우 null.</param>
    /// <param name="PayloadJson">JSON 문자열 — `Update` 시 변경할 필드들, `Add` 시 새 toilet 데이터. null 이면 빈 객체로 저장.</param>
    public sealed record ChangeRequestInput(ChangeRequestKind Kind, long? TargetToiletId, string? PayloadJson);

    /// <summary>관리자 검토 페이로드.</summary>
    /// <param name="Approve">true 승인 / false 반려.</param>
    /// <param name="Note">검토 메모 (선택). 사용자에게 노출 가능성 — 친절하게 작성 권장.</param>
    public sealed record ChangeRequestReview(bool Approve, string? Note);
}
