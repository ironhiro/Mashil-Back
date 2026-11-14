using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Import;

/// <summary>
/// 소스별 PostgreSQL 세션 advisory lock (DATA-22). 스케줄 실행·수동 실행·다중 인스턴스가 겹쳐도 하나만 돈다.
/// 락은 커넥션에 묶이므로 획득부터 해제까지 DbContext 의 커넥션을 열어 둔다.
/// </summary>
public sealed class ImportLock : IAsyncDisposable
{
    private const long KeyBase = 0x4D415348_00000000; // 'MASH'
    private readonly MashilDbContext _db;
    private readonly long _key;

    private ImportLock(MashilDbContext db, long key) { _db = db; _key = key; }

    public static long KeyFor(int sourceId) => KeyBase + sourceId;

    public static async Task<ImportLock?> TryAcquireAsync(MashilDbContext db, int sourceId, CancellationToken ct)
    {
        var key = KeyFor(sourceId);
        await db.Database.OpenConnectionAsync(ct);
        var ok = await db.Database.SqlQuery<bool>($"SELECT pg_try_advisory_lock({key}) AS \"Value\"").SingleAsync(ct);
        if (ok) return new ImportLock(db, key);

        await db.Database.CloseConnectionAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await _db.Database.SqlQuery<bool>($"SELECT pg_advisory_unlock({_key}) AS \"Value\"").SingleAsync();
        }
        finally
        {
            await _db.Database.CloseConnectionAsync();
        }
    }
}

public sealed class ImportBusyException(string sourceCode)
    : Exception($"{sourceCode} 임포트가 이미 실행 중입니다");
