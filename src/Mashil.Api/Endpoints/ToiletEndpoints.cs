using Mashil.Domain;
using Mashil.Infrastructure;
using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Mashil.Api.Endpoints;

public static class ToiletEndpoints
{
    /// <summary>검색 결과 전체 수를 담는 응답 헤더.</summary>
    public const string TotalCountHeader = "X-Total-Count";

    /// <summary>`/near` 반경 상한 (미터). 웹의 가장 큰 선택지 10km 와 같다.</summary>
    public const double MaxNearRadiusM = 10_000;

    /// <summary>
    /// 공개 API 에 보이는 화장실. 삭제 요청이 승인(Removed)된 곳은 목록·상세에 없고 평점·댓글도 받지 않는다 (CR-03, #18).
    /// 관리자는 <c>/api/admin/toilets/{id}</c> 로 계속 볼 수 있다.
    /// </summary>
    public static readonly Expression<Func<Toilet, bool>> IsVisible = t => t.Status != ToiletStatus.Removed;

    /// <summary>출처 표시 (MAP-07): 공공데이터 원본이면 LOCALDATA, 제보로 추가된 곳이면 USER_SUBMITTED.</summary>
    public static string SourceProvider(ToiletOrigin origin) => origin == ToiletOrigin.UserSubmitted ? "USER_SUBMITTED" : "LOCALDATA";

    public static void MapToiletEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/toilets").WithTags("Toilets");

        group.MapGet("/near", async (
            double lat,
            double lng,
            double radiusM,
            int? limit,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            // 반경은 화면에서 고를 수 있는 최대(10km)까지로 자른다. 자르지 않으면 큰 값 하나로 전체 행을 훑게 된다.
            var radius = Math.Clamp(radiusM, 1, MaxNearRadiusM);
            var results = await LoadNearAsync(db, lat, lng, radius, Math.Clamp(limit ?? 1000, 1, 5000), ct);
            return Results.Ok(results);
        })
        .WithName("ListNearbyToilets")
        .WithSummary("내 위치 기준 반경 내 화장실 (가까운 순)")
        .WithDescription("""
            `lat`/`lng` 중심에서 `radiusM` (미터, 1~10000 으로 클램프) 이내 + `Location` 이 채워진 행만 거리 오름차순 반환.
            거리는 PostGIS `ST_Distance` (geography) 결과로 미터 단위 (`distanceM`).
            `limit` 은 1~5000 사이로 클램프, 기본 1000. 반경이 자연 throttle 이라 보통 명시 불필요.
            `openStatus` 는 구조화된 개방시간으로 서버(KST)가 판정한 현재 상태 — 요일·휴무·공휴일·자정 넘김 반영.
            `openHours`/`openHoursDetail` 원문은 하위 호환과 표시용으로 남겨 둔다.
            """)
        .Produces<IReadOnlyList<ToiletNearDto>>();

        group.MapGet("/recommended", async (
            double lat,
            double lng,
            double? radiusM,
            int? limit,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            var radius = Math.Clamp(radiusM ?? 1000, 100, 5000);
            var take = Math.Clamp(limit ?? 5, 1, 20);
            var near = await LoadNearAsync(db, lat, lng, radius, 1000, ct);
            var ids = near.Select(t => t.Id).ToList();
            // 가려지지 않은 댓글이 받은 표만 센다
            var votes = await db.CommentVotes
                .Join(db.Comments, v => v.CommentId, c => c.Id, (v, c) => new { v.Value, c.ToiletId, c.Hidden })
                .Where(x => ids.Contains(x.ToiletId) && !x.Hidden)
                .GroupBy(x => x.ToiletId)
                .Select(g => new { g.Key, Up = g.Count(x => x.Value > 0), Down = g.Count(x => x.Value < 0) })
                .ToDictionaryAsync(x => x.Key, ct);

            var ranked = near
                .Select(t =>
                {
                    var rc = t.RatingCount;
                    var v = votes.GetValueOrDefault(t.Id);
                    var up = v?.Up ?? 0;
                    var down = v?.Down ?? 0;
                    var score = ToiletRecommender.Score(new RecommendInput(
                        t.DistanceM, radius, t.AverageRating, rc, up, down, t.OpenStatus.Kind));
                    return score is null ? null : new RecommendedToiletDto(t, score.Score, rc, up, down);
                })
                .Where(x => x is not null)
                .OrderByDescending(x => x!.Score)
                .ThenBy(x => x!.Toilet.DistanceM)
                .Take(take)
                .ToList();
            return Results.Ok(ranked);
        })
        .WithName("ListRecommendedToilets")
        .WithSummary("지금 여기서 가기 좋은 화장실 (추천순)")
        .WithDescription("""
            반경(`radiusM`, 기본 1000, 100~5000) 안에서 **닫히지 않았고** 평점이나 댓글 평가가 있는 곳만,
            점수 높은 순으로 `limit`(기본 5, 최대 20)건.
            점수 = 품질 50% (별점 베이지안 평균) + 믿음 30% (댓글들이 받은 도움돼요 비율) + 가까움 20%.
            열려 있는지 모르면 0.85 배. 자세한 식은 `ToiletRecommender`.
            """)
        .Produces<IReadOnlyList<RecommendedToiletDto>>();

        group.MapGet("/in-bounds", async (
            double north,
            double south,
            double east,
            double west,
            int? limit,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 200, 1, 500);
            var results = await db.Toilets
                .Where(t => t.Status != ToiletStatus.Removed
                         && t.Location != null
                         && t.Latitude >= south && t.Latitude <= north
                         && t.Longitude >= west && t.Longitude <= east)
                .Take(take)
                .Select(t => new ToiletInBoundsDto(
                    t.Id, t.Name, t.Kind,
                    t.RoadAddress, t.LotAddress,
                    t.Latitude, t.Longitude))
                .ToListAsync(ct);
            return Results.Ok(results);
        })
        .WithName("ListToiletsInBounds")
        .WithSummary("지도 viewport (bbox) 안 화장실")
        .WithDescription("""
            지도 표시용 — 4 모서리 좌표 (`north`/`south`/`east`/`west`) 안의 좌표 보유 행을 반환.
            거리 정렬 없음. `limit` 은 1~500 (기본 200) — 도심 viewport 가 보통 수백 이하라 충분.
            """)
        .Produces<IReadOnlyList<ToiletInBoundsDto>>();

        group.MapGet("/{id:long}", async (long id, MashilDbContext db, CancellationToken ct) =>
        {
            var x = await db.Toilets
                .Where(t => t.Id == id)
                .Where(IsVisible)
                .Select(t => new
                {
                    t.Id, t.ManagementNo, t.Name, t.Kind, t.KindCode, t.Status, t.Origin,
                    t.RoadAddress, t.LotAddress, t.Latitude, t.Longitude, t.CoordinateSource,
                    t.OpenHours, t.OpenHoursDetail, t.Phone, t.ScheduleStatus,
                    t.LocalGovCode, t.ManagementOrg,
                    Schedules = t.Schedules.Select(s => new ScheduleRow(s.DayType, s.OpenMinute, s.CloseMinute, s.IsClosed)).ToList(),
                    t.MaleStallCount, t.MaleUrinalCount, t.MaleAccessibleStallCount, t.MaleAccessibleUrinalCount,
                    t.MaleChildStallCount, t.MaleChildUrinalCount,
                    t.FemaleStallCount, t.FemaleAccessibleStallCount, t.FemaleChildStallCount,
                    t.EmergencyBellInstalled, t.EntranceCctv, t.DiaperChangerAvailable,
                    t.DataReferenceDate,
                    EditedByPeople = t.Overrides.Any(o => o.IsActive && (o.Source == OverrideSource.Admin || o.Source == OverrideSource.ChangeRequest)),
                    AverageRating = db.Ratings.Where(r => r.ToiletId == t.Id).Average(r => (double?)r.Score),
                    RatingCount = db.Ratings.Count(r => r.ToiletId == t.Id),
                })
                .AsSplitQuery()
                .FirstOrDefaultAsync(ct);
            if (x is null) return Results.NotFound();

            // 마지막 갱신일 (MAP-07): 원본을 마지막으로 받아 확인한 때. 같은 파일이라 건너뛴 실행도 "확인함"으로 친다
            var lastRefreshedAt = await db.ImportRuns
                .Where(r => r.Trigger != ImportTrigger.Renormalize
                         && (r.Status == ImportRunStatus.Succeeded || r.Status == ImportRunStatus.Skipped || r.Status == ImportRunStatus.PendingApproval))
                .MaxAsync(r => r.FinishedAt, ct);

            // 전화번호가 없으면 같은 관리기관의 대표 번호 (화면에는 "관리기관 전화"로 따로 표시)
            var orgPhone = x.Phone is null && x.ManagementOrg is not null
                ? await OrgPhone.FindAsync(db, x.LocalGovCode, x.ManagementOrg, ct)
                : null;

            var nowKst = OpenStatusMapper.NowKst();
            var holidays = await OpenStatusMapper.HolidaysAroundAsync(db, nowKst, ct);
            return Results.Ok(new ToiletDetailDto(
                x.Id, x.ManagementNo, x.Name, x.Kind,
                x.RoadAddress, x.LotAddress,
                x.Latitude, x.Longitude,
                x.OpenHours, x.OpenHoursDetail, x.Phone,
                x.KindCode, x.Status, x.ScheduleStatus,
                OpenStatusMapper.ToDtos(x.Schedules),
                OpenStatusMapper.Evaluate(x.ScheduleStatus, x.Schedules, nowKst, holidays),
                new FacilitiesDto(x.MaleStallCount, x.MaleUrinalCount, x.MaleAccessibleStallCount, x.MaleAccessibleUrinalCount,
                    x.MaleChildStallCount, x.MaleChildUrinalCount, x.FemaleStallCount, x.FemaleAccessibleStallCount, x.FemaleChildStallCount,
                    x.EmergencyBellInstalled, x.EntranceCctv, x.DiaperChangerAvailable),
                // 제보로 추가된 곳은 원본 파일과 무관하므로 원본 갱신 시각을 붙이지 않는다
                new SourceInfoDto(SourceProvider(x.Origin), x.DataReferenceDate, x.CoordinateSource, x.EditedByPeople,
                    x.Origin == ToiletOrigin.UserSubmitted ? null : lastRefreshedAt),
                x.AverageRating, x.RatingCount,
                x.ManagementOrg, orgPhone));
        })
        .WithName("GetToilet")
        .WithSummary("화장실 단건 상세")
        .WithDescription("""
            `id` 가 없거나 삭제 승인(`Removed`)된 곳이면 404. 상세 화면과 변경 요청 폼 prefill 에 쓴다.
            `schedules` 는 요일별 개방 시간대(공휴일 포함), `openStatus` 는 서버(KST) 판정, `source` 는 출처 표시(MAP-07, `provider` = `LOCALDATA` 또는 제보로 추가된 곳은 `USER_SUBMITTED`, 마지막 갱신 시각 포함), `averageRating`·`ratingCount` 는 평점 평균과 수.
            (NetTopologySuite `Point` 타입은 System.Text.Json 기본 직렬화 미지원이라 풀 엔티티 대신 DTO 사용.)
            """)
        .Produces<ToiletDetailDto>()
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/search", async (string q, int? limit, int? offset, double? lat, double? lng, MashilDbContext db, HttpResponse response, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var skip = Math.Max(offset ?? 0, 0);
            // 빈 값·공백만이면 전체가 아니라 빈 결과 (웹은 두 글자부터 검색한다)
            if (string.IsNullOrWhiteSpace(q))
            {
                response.Headers[TotalCountHeader] = "0";
                return Results.Ok(Array.Empty<ToiletSearchDto>());
            }
            var like = ContainsPattern(q);
            var matches = db.Toilets
                .Where(t => t.Status != ToiletStatus.Removed
                         && (EF.Functions.ILike(t.Name, like, LikeEscape)
                             || (t.RoadAddress != null && EF.Functions.ILike(t.RoadAddress, like, LikeEscape))
                             || (t.LotAddress != null && EF.Functions.ILike(t.LotAddress, like, LikeEscape))));
            // 맞는 곳 전체 수. 목록은 limit 만큼씩 끊어 주므로 화면이 "N곳 중 몇 곳" 과 "더 보기" 를 그릴 수 있게 헤더로 준다
            response.Headers[TotalCountHeader] = (await matches.CountAsync(ct)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            // 위치를 주면 가까운 곳부터 (위치 미확정 행은 뒤로). 없으면 이름순
            if (lat is { } la && lng is { } ln && la is >= -90 and <= 90 && ln is >= -180 and <= 180)
            {
                var origin = new Point(ln, la) { SRID = 4326 };
                matches = matches.OrderBy(t => t.Location == null).ThenBy(t => t.Location!.Distance(origin));
            }
            else
            {
                matches = matches.OrderBy(t => t.Name).ThenBy(t => t.Id);
            }
            var results = await matches
                .Skip(skip)
                .Take(take)
                .Select(t => new ToiletSearchDto(
                    t.Id, t.Name,
                    t.RoadAddress, t.LotAddress,
                    t.Latitude, t.Longitude))
                .ToListAsync(ct);
            return Results.Ok(results);
        })
        .WithName("SearchToilets")
        .WithSummary("이름/주소 텍스트 검색 (ILIKE)")
        .WithDescription("`q`(앞뒤 공백 제외, 글자 그대로 — `%`·`_` 도 와일드카드가 아님) 로 `Name`, `RoadAddress`, `LotAddress` 를 OR ILIKE 부분 일치. 빈 값·공백만이면 빈 목록(`X-Total-Count: 0`). 좌표 없는 행도 포함 (지도 미표시 행 검색용). `lat`·`lng` 를 주면 가까운 순(좌표 없는 행은 뒤), 없으면 이름순. `limit`(기본 50, 최대 200)·`offset` 으로 나눠 받고, 맞는 곳 전체 수는 `X-Total-Count` 헤더.")
        .Produces<IReadOnlyList<ToiletSearchDto>>();
    }

    /// <summary>ILIKE 이스케이프 문자.</summary>
    public const string LikeEscape = "\\";

    /// <summary>
    /// 검색어 → 부분 일치 ILIKE 패턴. 앞뒤 공백을 빼고 <c>\</c>, <c>%</c>, <c>_</c> 를 이스케이프해 글자 그대로 찾는다.
    /// 이스케이프하지 않으면 <c>_</c> 한 글자로 전체가 맞는다 (#19).
    /// </summary>
    public static string ContainsPattern(string q)
    {
        var escaped = q.Trim().Replace(LikeEscape, LikeEscape + LikeEscape).Replace("%", LikeEscape + "%").Replace("_", LikeEscape + "_");
        return $"%{escaped}%";
    }

    /// <summary>반경 안 화장실 (가까운 순) + 평점·댓글 집계 + 서버 영업 판정. /near 와 /recommended 가 함께 쓴다.</summary>
    internal static async Task<List<ToiletNearDto>> LoadNearAsync(
        MashilDbContext db, double lat, double lng, double radiusM, int take, CancellationToken ct)
    {
        var origin = new Point(lng, lat) { SRID = 4326 };

        var rows = await db.Toilets
            .Where(t => t.Status != ToiletStatus.Removed
                     && t.Location != null && t.Location.IsWithinDistance(origin, radiusM))
            .OrderBy(t => t.Location!.Distance(origin))
            .Take(take)
            .Select(t => new
            {
                t.Id, t.Name, t.Kind, t.KindCode,
                t.RoadAddress, t.LotAddress,
                t.Latitude, t.Longitude,
                DistanceM = t.Location!.Distance(origin),
                t.MaleStallCount, t.MaleUrinalCount,
                t.MaleAccessibleStallCount, t.MaleAccessibleUrinalCount,
                t.MaleChildStallCount, t.MaleChildUrinalCount,
                t.FemaleStallCount, t.FemaleAccessibleStallCount, t.FemaleChildStallCount,
                t.OpenHours, t.OpenHoursDetail, t.Phone,
                t.ScheduleStatus,
                Schedules = t.Schedules.Select(s => new ScheduleRow(s.DayType, s.OpenMinute, s.CloseMinute, s.IsClosed)).ToList(),
                // 평점 / 댓글 집계 — EF 가 SELECT subquery 로 변환.
                // (double?) 캐스팅으로 평점 행 0개일 때 null 반환되게 함.
                AverageRating = db.Ratings.Where(r => r.ToiletId == t.Id).Average(r => (double?)r.Score),
                RatingCount = db.Ratings.Count(r => r.ToiletId == t.Id),
                CommentCount = db.Comments.Count(c => c.ToiletId == t.Id && !c.Hidden),
            })
            .AsSplitQuery()
            .ToListAsync(ct);

        var nowKst = OpenStatusMapper.NowKst();
        var holidays = await OpenStatusMapper.HolidaysAroundAsync(db, nowKst, ct);
        var results = rows.Select(t => new ToiletNearDto(
            t.Id, t.Name, t.Kind,
            t.RoadAddress, t.LotAddress,
            t.Latitude, t.Longitude,
            t.DistanceM,
            t.MaleStallCount, t.MaleUrinalCount,
            t.MaleAccessibleStallCount, t.MaleAccessibleUrinalCount,
            t.MaleChildStallCount, t.MaleChildUrinalCount,
            t.FemaleStallCount, t.FemaleAccessibleStallCount, t.FemaleChildStallCount,
            t.OpenHours, t.OpenHoursDetail, t.Phone,
            t.AverageRating, t.RatingCount, t.CommentCount,
            t.KindCode, t.ScheduleStatus,
            OpenStatusMapper.Evaluate(t.ScheduleStatus, t.Schedules, nowKst, holidays))).ToList();
        return results;
    }
}

/// <summary>추천 한 건: 근처 목록과 같은 화장실 정보 + 점수와 근거 (평점 수, 댓글이 받은 도움돼요/아니에요).</summary>
public sealed record RecommendedToiletDto(ToiletNearDto Toilet, double Score, int RatingCount, int HelpfulUp, int HelpfulDown);

/// <summary>
/// 가까운 화장실 검색(`/near`) 응답 행. <c>DistanceM</c> 은 검색 중심점에서의 거리 (미터, PostGIS <c>ST_Distance</c> geography 결과).
/// 시설 정보(변기 수), 영업 정보(영업시간/전화), 평점·댓글 집계까지 한 번에.
/// <c>AverageRating</c> 은 평점 행이 없으면 null, <c>RatingCount</c> 는 그 평균을 낸 평점 수. <c>CommentCount</c> 는 hidden 제외.
/// </summary>
public sealed record ToiletNearDto(
    long Id,
    string Name,
    string Kind,
    string? RoadAddress,
    string? LotAddress,
    double? Latitude,
    double? Longitude,
    double DistanceM,
    int MaleStallCount,
    int MaleUrinalCount,
    int MaleAccessibleStallCount,
    int MaleAccessibleUrinalCount,
    int MaleChildStallCount,
    int MaleChildUrinalCount,
    int FemaleStallCount,
    int FemaleAccessibleStallCount,
    int FemaleChildStallCount,
    string? OpenHours,
    string? OpenHoursDetail,
    string? Phone,
    double? AverageRating,
    int RatingCount,
    int CommentCount,
    ToiletKind KindCode,
    ScheduleStatus ScheduleStatus,
    OpenStatusDto OpenStatus);

/// <summary>지도 viewport(`/in-bounds`) 응답 행 — 거리 없음.</summary>
public sealed record ToiletInBoundsDto(
    long Id,
    string Name,
    string Kind,
    string? RoadAddress,
    string? LotAddress,
    double? Latitude,
    double? Longitude);

/// <summary>텍스트 검색(`/search`) 응답 행 — 좌표 없는 행도 포함.</summary>
public sealed record ToiletSearchDto(
    long Id,
    string Name,
    string? RoadAddress,
    string? LotAddress,
    double? Latitude,
    double? Longitude);

/// <summary>
/// 단건 상세(`/{id}`) 응답 — 변경 요청 폼 prefill 용.
/// 풀 Toilet 엔티티 대신 사용 (NTS Point 직렬화 회피 + 사용자에게 노출 안 할 메타필드 제외).
/// </summary>
public sealed record ToiletDetailDto(
    long Id,
    string ManagementNo,
    string Name,
    string Kind,
    string? RoadAddress,
    string? LotAddress,
    double? Latitude,
    double? Longitude,
    string? OpenHours,
    string? OpenHoursDetail,
    string? Phone,
    ToiletKind KindCode,
    ToiletStatus Status,
    ScheduleStatus ScheduleStatus,
    IReadOnlyList<ScheduleDto> Schedules,
    OpenStatusDto OpenStatus,
    FacilitiesDto Facilities,
    SourceInfoDto Source,
    double? AverageRating,
    int RatingCount,
    string? ManagementOrg = null,
    string? OrgPhone = null);

/// <summary>시설 수와 편의시설.</summary>
public sealed record FacilitiesDto(
    int MaleStall, int MaleUrinal, int MaleAccessibleStall, int MaleAccessibleUrinal,
    int MaleChildStall, int MaleChildUrinal, int FemaleStall, int FemaleAccessibleStall, int FemaleChildStall,
    bool? EmergencyBell, bool? EntranceCctv, bool? DiaperChanger);

/// <summary>
/// 출처 표시 (MAP-07). <c>EditedByPeople</c> = 관리자 수정이나 승인된 변경 요청이 반영됨.
/// <c>LastRefreshedAt</c> = 원본 파일을 마지막으로 받아 반영·확인한 시각 (UTC, 임포트 기록이 없으면 null).
/// </summary>
public sealed record SourceInfoDto(string Provider, DateOnly? DataReferenceDate, CoordinateSource CoordinateSource, bool EditedByPeople, DateTime? LastRefreshedAt);

/// <summary>
/// 관리기관 대표 번호: 전화번호가 빈 화장실에, 같은 자치단체·같은 관리기관의 다른 화장실들이 쓰는 번호 중
/// 과반이 쓰는 번호. 기관이 화장실마다 다른 번호를 쓰면(과반 없음) 고르지 않는다 — 엉뚱한 곳에 전화하게 하지 않으려고.
/// 화장실 자체 번호가 아니라서 Phone 에 넣지 않고 OrgPhone 으로 따로 준다 (일일 임포트의 변경 비교에도 섞이지 않게).
/// </summary>
public static class OrgPhone
{
    public static async Task<string?> FindAsync(MashilDbContext db, int localGovCode, string managementOrg, CancellationToken ct)
    {
        var counts = await db.Toilets.AsNoTracking()
            .Where(t => t.Status != ToiletStatus.Removed && t.LocalGovCode == localGovCode && t.ManagementOrg == managementOrg && t.Phone != null)
            .GroupBy(t => t.Phone!)
            .Select(g => new { Phone = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        return Pick(counts.Select(c => (c.Phone, c.Count)));
    }

    /// <summary>과반이 쓰는 번호, 없으면 null.</summary>
    public static string? Pick(IEnumerable<(string Phone, int Count)> counts)
    {
        var list = counts.ToList();
        var total = list.Sum(c => c.Count);
        var top = list.OrderByDescending(c => c.Count).ThenBy(c => c.Phone, StringComparer.Ordinal).FirstOrDefault();
        return total > 0 && top.Count * 2 > total ? top.Phone : null;
    }
}
