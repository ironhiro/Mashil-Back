using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Api.Endpoints;

/// <summary>요일 하나의 개방 시간대. 시각은 "HH:mm" (24:00 가능).</summary>
public sealed record ScheduleDto(DayType Day, string? Open, string? Close, bool Closed);

public sealed record NextOpenDto(int DaysFromToday, DayType Day, string At);

/// <summary>
/// 서버가 판정한 현재 개방 상태 (MAP-05). 기준 시각은 KST.
/// <c>Label</c> 은 그대로 보여 줄 수 있는 한국어 문구 (앱·웹 공통).
/// </summary>
public sealed record OpenStatusDto(OpenStatusKind Kind, string? ClosesAt, NextOpenDto? NextOpen, string Label);

/// <summary>EF 투영용 스케줄 행.</summary>
public sealed record ScheduleRow(DayType Day, int? Open, int? Close, bool Closed);

public static class OpenStatusMapper
{
    public static DateTime NowKst() => DateTime.UtcNow.AddHours(9);

    /// <summary>오늘 앞뒤 일주일 범위의 공휴일 (판정에 필요한 만큼만).</summary>
    public static async Task<HashSet<DateOnly>> HolidaysAroundAsync(MashilDbContext db, DateTime nowKst, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(nowKst);
        var list = await db.PublicHolidays
            .Where(h => h.Date >= today.AddDays(-1) && h.Date <= today.AddDays(8))
            .Select(h => h.Date).ToListAsync(ct);
        return [.. list];
    }

    public static OpenStatusDto Evaluate(ScheduleStatus status, IReadOnlyCollection<ScheduleRow> rows, DateTime nowKst, HashSet<DateOnly> holidays)
    {
        var schedules = rows.Select(r => new OpeningSchedule { DayType = r.Day, OpenMinute = r.Open, CloseMinute = r.Close, IsClosed = r.Closed }).ToList();
        var s = OpenStatusEvaluator.Evaluate(status, schedules, nowKst, holidays.Contains);
        var closes = s.ClosesAtMinute is { } c ? OpenStatusEvaluator.FormatMinute(c) : null;
        var next = s.NextOpen is { } n ? new NextOpenDto(n.DaysFromToday, n.Day, OpenStatusEvaluator.FormatMinute(n.Minute)) : null;
        return new OpenStatusDto(s.Kind, closes, next, Label(status, s.Kind, closes, next));
    }

    public static IReadOnlyList<ScheduleDto> ToDtos(IEnumerable<ScheduleRow> rows) => rows
        .OrderBy(r => r.Day).ThenBy(r => r.Open)
        .Select(r => new ScheduleDto(r.Day,
            r.Open is { } o ? OpenStatusEvaluator.FormatMinute(o) : null,
            r.Close is { } c ? OpenStatusEvaluator.FormatMinute(c) : null,
            r.Closed))
        .ToList();

    private static readonly Dictionary<DayType, string> DayKo = new()
    {
        [DayType.Mon] = "월", [DayType.Tue] = "화", [DayType.Wed] = "수", [DayType.Thu] = "목",
        [DayType.Fri] = "금", [DayType.Sat] = "토", [DayType.Sun] = "일", [DayType.Holiday] = "공휴일",
    };

    private static string Label(ScheduleStatus status, OpenStatusKind kind, string? closes, NextOpenDto? next) => kind switch
    {
        OpenStatusKind.AlwaysOpen => "24시간",
        OpenStatusKind.Open => closes == "24:00" ? "영업중 · 자정까지" : $"영업중 · {closes}까지",
        OpenStatusKind.Closed when next is null => "지금은 닫혀 있어요",
        OpenStatusKind.Closed => next.DaysFromToday switch
        {
            0 => $"닫힘 · 오늘 {next.At}에 열려요",
            1 => $"닫힘 · 내일({DayKo[next.Day]}) {next.At}에 열려요",
            _ => $"닫힘 · {DayKo[next.Day]}요일 {next.At}에 열려요",
        },
        _ => status switch
        {
            ScheduleStatus.NotOpen => "일반 개방 안 함",
            ScheduleStatus.Irregular => "불규칙 운영 · 직접 확인",
            ScheduleStatus.FollowsFacility => "건물 운영시간 내",
            ScheduleStatus.Scheduled => "오늘 운영 정보 없음",
            _ => "운영 정보 없음",
        },
    };
}
