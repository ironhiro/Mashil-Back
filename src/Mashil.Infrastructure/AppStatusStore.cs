using Mashil.Domain;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Infrastructure;

/// <summary>점검 상태 한 장 (OPS-05). <c>GET /api/app/status</c> 응답과 같은 모양.</summary>
public sealed record AppStatusSnapshot(bool Maintenance, bool ReadOnly, string? Message, DateTimeOffset? Until)
{
    public static readonly AppStatusSnapshot Normal = new(false, false, null, null);

    /// <summary>전체 점검이나 읽기 전용이면 일반 사용자의 쓰기를 막는다.</summary>
    public bool WritesBlocked => Maintenance || ReadOnly;
}

/// <summary>
/// 점검 상태 저장소 (ADM-06). DB 의 <c>app_status</c> 1행 + 설정(App:Status) 폴백.
/// <list type="bullet">
/// <item>DB 에 행이 없거나 DB 에 닿지 못하면 설정 값 그대로</item>
/// <item>설정에서 켠 플래그는 DB 값과 상관없이 켜진다 (DB 가 문제일 때 환경 변수로 켜는 비상 스위치)</item>
/// <item>문구·종료 예정 시각은 DB 값 우선, 비어 있으면 설정 값</item>
/// </list>
/// </summary>
public static class AppStatusStore
{
    public static AppStatusSnapshot Merge(AppStatusSetting? row, AppStatusSnapshot config) =>
        row is null
            ? config
            : new AppStatusSnapshot(
                row.Maintenance || config.Maintenance,
                row.ReadOnly || config.ReadOnly,
                string.IsNullOrWhiteSpace(row.Message) ? config.Message : row.Message,
                row.Until ?? config.Until);

    public static Task<AppStatusSetting?> LoadAsync(MashilDbContext db, CancellationToken ct) =>
        db.AppStatusSettings.AsNoTracking().SingleOrDefaultAsync(s => s.Id == AppStatusSetting.SingletonId, ct);

    /// <summary>DB 행을 쓴다 (없으면 만든다).</summary>
    public static async Task<AppStatusSetting> SaveAsync(MashilDbContext db, bool maintenance, bool readOnly, string? message,
        DateTimeOffset? until, Guid? adminId, DateTime now, CancellationToken ct)
    {
        var row = await db.AppStatusSettings.SingleOrDefaultAsync(s => s.Id == AppStatusSetting.SingletonId, ct);
        if (row is null)
        {
            row = new AppStatusSetting();
            db.AppStatusSettings.Add(row);
        }
        row.Maintenance = maintenance;
        row.ReadOnly = readOnly;
        row.Message = string.IsNullOrWhiteSpace(message) ? null : message.Trim() is { Length: > 500 } m ? m[..500] : message.Trim();
        row.Until = until?.ToUniversalTime();
        row.UpdatedById = adminId;
        row.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        return row;
    }
}
