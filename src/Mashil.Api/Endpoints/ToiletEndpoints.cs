using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;

namespace Mashil.Api.Endpoints;

public static class ToiletEndpoints
{
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
            var origin = new Point(lng, lat) { SRID = 4326 };
            // 디폴트 1000 / 상한 5000 — 반경(radiusM) 이 자연 throttle.
            // 도심 + 큰 반경에서 수천 건 반환될 수 있으니 클라이언트가 처리 부담 고려.
            var take = Math.Clamp(limit ?? 1000, 1, 5000);

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
                t.AverageRating, t.CommentCount,
                t.KindCode, t.ScheduleStatus,
                OpenStatusMapper.Evaluate(t.ScheduleStatus, t.Schedules, nowKst, holidays))).ToList();

            return Results.Ok(results);
        })
        .WithName("ListNearbyToilets")
        .WithSummary("내 위치 기준 반경 내 화장실 (가까운 순)")
        .WithDescription("""
            `lat`/`lng` 중심에서 `radiusM` (미터) 이내 + `Location` 이 채워진 행만 거리 오름차순 반환.
            거리는 PostGIS `ST_Distance` (geography) 결과로 미터 단위 (`distanceM`).
            `limit` 은 1~5000 사이로 클램프, 기본 1000. 반경이 자연 throttle 이라 보통 명시 불필요.
            `openStatus` 는 구조화된 개방시간으로 서버(KST)가 판정한 현재 상태 — 요일·휴무·공휴일·자정 넘김 반영.
            `openHours`/`openHoursDetail` 원문은 하위 호환과 표시용으로 남겨 둔다.
            """)
        .Produces<IReadOnlyList<ToiletNearDto>>();

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
                .Select(t => new
                {
                    t.Id, t.ManagementNo, t.Name, t.Kind, t.KindCode, t.Status,
                    t.RoadAddress, t.LotAddress, t.Latitude, t.Longitude, t.CoordinateSource,
                    t.OpenHours, t.OpenHoursDetail, t.Phone, t.ScheduleStatus,
                    Schedules = t.Schedules.Select(s => new ScheduleRow(s.DayType, s.OpenMinute, s.CloseMinute, s.IsClosed)).ToList(),
                    t.MaleStallCount, t.MaleUrinalCount, t.MaleAccessibleStallCount, t.MaleAccessibleUrinalCount,
                    t.MaleChildStallCount, t.MaleChildUrinalCount,
                    t.FemaleStallCount, t.FemaleAccessibleStallCount, t.FemaleChildStallCount,
                    t.EmergencyBellInstalled, t.EntranceCctv, t.DiaperChangerAvailable,
                    t.DataReferenceDate,
                    EditedByPeople = t.Overrides.Any(o => o.IsActive && (o.Source == OverrideSource.Admin || o.Source == OverrideSource.ChangeRequest)),
                })
                .AsSplitQuery()
                .FirstOrDefaultAsync(ct);
            if (x is null) return Results.NotFound();

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
                new SourceInfoDto("LOCALDATA", x.DataReferenceDate, x.CoordinateSource, x.EditedByPeople)));
        })
        .WithName("GetToilet")
        .WithSummary("화장실 단건 상세")
        .WithDescription("""
            `id` 가 없으면 404. 상세 화면과 변경 요청 폼 prefill 에 쓴다.
            `schedules` 는 요일별 개방 시간대(공휴일 포함), `openStatus` 는 서버(KST) 판정, `source` 는 출처 표시(MAP-07).
            (NetTopologySuite `Point` 타입은 System.Text.Json 기본 직렬화 미지원이라 풀 엔티티 대신 DTO 사용.)
            """)
        .Produces<ToiletDetailDto>()
        .Produces(StatusCodes.Status404NotFound);

        group.MapGet("/search", async (string q, int? limit, MashilDbContext db, CancellationToken ct) =>
        {
            var take = Math.Clamp(limit ?? 50, 1, 200);
            var like = $"%{q}%";
            var results = await db.Toilets
                .Where(t => t.Status != ToiletStatus.Removed
                         && (EF.Functions.ILike(t.Name, like)
                             || (t.RoadAddress != null && EF.Functions.ILike(t.RoadAddress, like))
                             || (t.LotAddress != null && EF.Functions.ILike(t.LotAddress, like))))
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
        .WithDescription("`q` 로 `Name`, `RoadAddress`, `LotAddress` 를 OR ILIKE 매칭. 좌표 없는 행도 포함 (지도 미표시 행 검색용).")
        .Produces<IReadOnlyList<ToiletSearchDto>>();
    }
}

/// <summary>
/// 가까운 화장실 검색(`/near`) 응답 행. <c>DistanceM</c> 은 검색 중심점에서의 거리 (미터, PostGIS <c>ST_Distance</c> geography 결과).
/// 시설 정보(변기 수), 영업 정보(영업시간/전화), 평점·댓글 집계까지 한 번에.
/// <c>AverageRating</c> 은 평점 행이 없으면 null. <c>CommentCount</c> 는 hidden 제외.
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
    SourceInfoDto Source);

/// <summary>시설 수와 편의시설.</summary>
public sealed record FacilitiesDto(
    int MaleStall, int MaleUrinal, int MaleAccessibleStall, int MaleAccessibleUrinal,
    int MaleChildStall, int MaleChildUrinal, int FemaleStall, int FemaleAccessibleStall, int FemaleChildStall,
    bool? EmergencyBell, bool? EntranceCctv, bool? DiaperChanger);

/// <summary>출처 표시 (MAP-07). <c>EditedByPeople</c> = 관리자 수정이나 승인된 변경 요청이 반영됨.</summary>
public sealed record SourceInfoDto(string Provider, DateOnly? DataReferenceDate, CoordinateSource CoordinateSource, bool EditedByPeople);
