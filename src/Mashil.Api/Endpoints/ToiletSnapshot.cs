using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Api.Endpoints;

/// <summary>
/// 전국 화장실 목록 한 벌 (`GET /api/toilets/snapshot`). 앱이 통째로 받아 근처 찾기·추천·검색을 기기에서 한다.
/// 사용자 위치가 서버로 오지 않게 하려는 것 (위치정보법 검토 2026-11: 위치를 단말기에서만 쓰면 위치기반서비스사업 신고 대상이 아님).
/// <para>
/// 형식: <c>{ version, fields, holidays, rows }</c>. <c>rows</c> 는 <c>fields</c> 순서의 배열 (키 반복을 줄여 크기를 줄인다).
/// <c>schedules</c> 칸은 <c>[요일(1=월 … 7=일, 8=공휴일), 여는 분, 닫는 분, 휴무 0/1]</c> 의 배열이거나 null.
/// 평점·댓글 수처럼 자주 바뀌는 값은 넣지 않는다 (<see cref="ToiletStatsProvider"/>): 이 목록은 하루 한 번 바뀌므로
/// 내용이 같으면 ETag 가 같아 304 로 끝난다.
/// </para>
/// </summary>
public sealed class ToiletSnapshotProvider(IServiceScopeFactory scopes, TimeProvider time, ILogger<ToiletSnapshotProvider> logger)
{
    /// <summary>이보다 오래된 사본은 다시 만든다 (일일 갱신·관리자 수정을 이 안에 반영).</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromHours(1);

    public const int Version = 1;

    public static readonly string[] Fields =
    [
        "id", "name", "kind", "kindCode", "roadAddress", "lotAddress", "lat", "lng",
        "maleStall", "maleUrinal", "maleAccessibleStall", "maleAccessibleUrinal", "maleChildStall", "maleChildUrinal",
        "femaleStall", "femaleAccessibleStall", "femaleChildStall",
        "openHours", "openHoursDetail", "phone", "scheduleStatus", "schedules",
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Snapshot? _current;
    private int _refreshing;

    public sealed record Snapshot(string ETag, byte[] Gzip, byte[] Brotli, DateTimeOffset BuiltAt, int Count);

    /// <summary>
    /// 지금 사본. 처음 한 번만 다 만들 때까지 기다리고, 그 뒤로는 오래됐어도 바로 돌려주면서 뒤에서 다시 만든다
    /// (만드는 데 수 초가 걸려 요청을 붙잡지 않으려고). 서버가 뜰 때 <see cref="ToiletSnapshotWarmup"/> 이 미리 만든다.
    /// </summary>
    public async Task<Snapshot> GetAsync(CancellationToken ct)
    {
        var c = _current;
        if (c is null) return await RefreshAsync(ct);
        if (time.GetUtcNow() - c.BuiltAt >= MaxAge) RebuildInBackground();
        return c;
    }

    /// <summary>
    /// 관리자 승인·수정처럼 화장실이 바뀐 직후 부른다: 한 시간을 기다리지 않고 뒤에서 바로 다시 만든다.
    /// 다 만들어지기 전까지는 지난 사본을 준다 (몇 초).
    /// </summary>
    public void MarkStale()
    {
        if (_current is { } c) _current = c with { BuiltAt = DateTimeOffset.MinValue };
        RebuildInBackground();
    }

    private void RebuildInBackground()
    {
        if (Interlocked.CompareExchange(ref _refreshing, 1, 0) != 0) return;
        _ = Task.Run(async () =>
        {
            try { await RefreshAsync(CancellationToken.None); }
            catch (Exception e) { logger.LogError(e, "Toilet snapshot background rebuild failed"); }
            finally { Volatile.Write(ref _refreshing, 0); }
        }, CancellationToken.None);
    }

    private async Task<Snapshot> RefreshAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var c = _current;
            if (c is not null && time.GetUtcNow() - c.BuiltAt < MaxAge) return c;
            try
            {
                _current = await BuildAsync(ct);
            }
            catch (Exception e) when (c is not null && e is not OperationCanceledException)
            {
                // 다시 만들다 실패하면 지난 사본을 계속 준다 (한 시간 뒤 다시 시도)
                logger.LogError(e, "Toilet snapshot rebuild failed; serving the previous one");
                _current = c with { BuiltAt = time.GetUtcNow() };
            }
            return _current;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<Snapshot> BuildAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MashilDbContext>();
        var started = time.GetTimestamp();

        var rows = await db.Toilets.AsNoTracking()
            .Where(ToiletEndpoints.IsVisible)
            .OrderBy(t => t.Id)
            .Select(t => new SnapshotRow(
                t.Id, t.Name, t.Kind, t.KindCode, t.RoadAddress, t.LotAddress,
                t.Location == null ? null : t.Latitude, t.Location == null ? null : t.Longitude,
                t.MaleStallCount, t.MaleUrinalCount, t.MaleAccessibleStallCount, t.MaleAccessibleUrinalCount,
                t.MaleChildStallCount, t.MaleChildUrinalCount,
                t.FemaleStallCount, t.FemaleAccessibleStallCount, t.FemaleChildStallCount,
                t.OpenHours, t.OpenHoursDetail, t.Phone, t.ScheduleStatus,
                t.Schedules.OrderBy(s => s.DayType).ThenBy(s => s.OpenMinute)
                    .Select(s => new ScheduleRow(s.DayType, s.OpenMinute, s.CloseMinute, s.IsClosed)).ToList()))
            .AsSplitQuery()
            .ToListAsync(ct);

        // 공휴일: 올해 1월 1일 ~ 내년 12월 31일. 범위를 날짜로 고정해야 내용이 매일 바뀌지 않는다
        var year = OpenStatusMapper.NowKst().Year;
        var holidays = await db.PublicHolidays.AsNoTracking()
            .Where(h => h.Date >= new DateOnly(year, 1, 1) && h.Date <= new DateOnly(year + 1, 12, 31))
            .OrderBy(h => h.Date)
            .Select(h => h.Date)
            .ToListAsync(ct);

        var queried = time.GetElapsedTime(started);
        var json = Serialize(rows, holidays);
        var etag = "\"" + Convert.ToHexStringLower(SHA256.HashData(json))[..32] + "\"";
        var built = new Snapshot(etag, Compress(json, gzip: true), Compress(json, gzip: false), time.GetUtcNow(), rows.Count);
        logger.LogInformation("Toilet snapshot built: {Count} rows, {Raw} bytes raw, {Gzip} gzip, {Brotli} br, query {QueryMs} ms, total {Ms} ms",
            rows.Count, json.Length, built.Gzip.Length, built.Brotli.Length, (int)queried.TotalMilliseconds, (int)time.GetElapsedTime(started).TotalMilliseconds);
        return built;
    }

    public sealed record SnapshotRow(
        long Id, string Name, string Kind, ToiletKind KindCode, string? RoadAddress, string? LotAddress,
        double? Latitude, double? Longitude,
        int MaleStall, int MaleUrinal, int MaleAccessibleStall, int MaleAccessibleUrinal, int MaleChildStall, int MaleChildUrinal,
        int FemaleStall, int FemaleAccessibleStall, int FemaleChildStall,
        string? OpenHours, string? OpenHoursDetail, string? Phone, ScheduleStatus ScheduleStatus,
        List<ScheduleRow> Schedules);

    /// <summary>목록을 JSON 바이트로 (UTF-8, 한글 그대로). <see cref="Fields"/> 순서와 같아야 한다.</summary>
    public static byte[] Serialize(IReadOnlyList<SnapshotRow> rows, IReadOnlyList<DateOnly> holidays)
    {
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            w.WriteStartObject();
            w.WriteNumber("version", Version);
            w.WriteStartArray("fields");
            foreach (var f in Fields) w.WriteStringValue(f);
            w.WriteEndArray();
            w.WriteStartArray("holidays");
            foreach (var h in holidays) w.WriteStringValue(h.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture));
            w.WriteEndArray();
            w.WriteStartArray("rows");
            foreach (var r in rows)
            {
                w.WriteStartArray();
                w.WriteNumberValue(r.Id);
                w.WriteStringValue(r.Name);
                w.WriteStringValue(r.Kind);
                w.WriteStringValue(r.KindCode.ToString());
                WriteNullable(w, r.RoadAddress);
                // 지번 주소가 도로명 주소와 같으면 빼서 크기를 줄인다 (받는 쪽은 null 이면 도로명 주소를 쓰지 않는다: 원래 null 인 것과 구분 안 함)
                WriteNullable(w, r.LotAddress == r.RoadAddress ? null : r.LotAddress);
                WriteCoord(w, r.Latitude);
                WriteCoord(w, r.Longitude);
                w.WriteNumberValue(r.MaleStall);
                w.WriteNumberValue(r.MaleUrinal);
                w.WriteNumberValue(r.MaleAccessibleStall);
                w.WriteNumberValue(r.MaleAccessibleUrinal);
                w.WriteNumberValue(r.MaleChildStall);
                w.WriteNumberValue(r.MaleChildUrinal);
                w.WriteNumberValue(r.FemaleStall);
                w.WriteNumberValue(r.FemaleAccessibleStall);
                w.WriteNumberValue(r.FemaleChildStall);
                WriteNullable(w, r.OpenHours);
                WriteNullable(w, r.OpenHoursDetail);
                WriteNullable(w, r.Phone);
                w.WriteStringValue(r.ScheduleStatus.ToString());
                if (r.Schedules.Count == 0)
                {
                    w.WriteNullValue();
                }
                else
                {
                    w.WriteStartArray();
                    foreach (var s in r.Schedules)
                    {
                        w.WriteStartArray();
                        w.WriteNumberValue((int)s.Day);
                        if (s.Open is { } o) w.WriteNumberValue(o); else w.WriteNullValue();
                        if (s.Close is { } c) w.WriteNumberValue(c); else w.WriteNullValue();
                        w.WriteNumberValue(s.Closed ? 1 : 0);
                        w.WriteEndArray();
                    }
                    w.WriteEndArray();
                }
                w.WriteEndArray();
            }
            w.WriteEndArray();
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    private static void WriteNullable(Utf8JsonWriter w, string? s)
    {
        if (s is null) w.WriteNullValue(); else w.WriteStringValue(s);
    }

    /// <summary>소수 여섯째 자리 (약 0.1m) 까지만.</summary>
    private static void WriteCoord(Utf8JsonWriter w, double? v)
    {
        if (v is { } d) w.WriteNumberValue(Math.Round(d, 6)); else w.WriteNullValue();
    }

    /// <summary>brotli 품질. 11(최대)은 16MB 에 수십 초라 9 로 (크기 차이는 몇 % 안 된다).</summary>
    private const int BrotliQuality = 9;

    private static byte[] Compress(byte[] data, bool gzip)
    {
        using var ms = new MemoryStream();
        if (gzip)
        {
            using var z = new GZipStream(ms, CompressionLevel.Optimal);
            z.Write(data);
        }
        else
        {
            using var z = new BrotliStream(ms, new BrotliCompressionOptions { Quality = BrotliQuality });
            z.Write(data);
        }
        return ms.ToArray();
    }

    /// <summary>If-None-Match 가 이 ETag 와 맞는지 (여러 값, 약한 비교 <c>W/</c>, <c>*</c> 허용).</summary>
    public static bool Matches(string? ifNoneMatch, string etag)
    {
        if (string.IsNullOrWhiteSpace(ifNoneMatch)) return false;
        foreach (var raw in ifNoneMatch.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (raw == "*") return true;
            var v = raw.StartsWith("W/", StringComparison.Ordinal) ? raw[2..] : raw;
            if (v == etag) return true;
        }
        return false;
    }

    /// <summary>Accept-Encoding 으로 고른 압축: br, gzip, 없으면 null (압축 안 함). q=0 은 거부로 본다.</summary>
    public static string? PickEncoding(string? acceptEncoding)
    {
        if (string.IsNullOrWhiteSpace(acceptEncoding)) return null;
        var accepted = acceptEncoding.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Split(';', StringSplitOptions.TrimEntries))
            .Where(p => !p.Skip(1).Any(x => x.Replace(" ", "") is "q=0" or "q=0.0" or "q=0.00" or "q=0.000"))
            .Select(p => p[0].ToLowerInvariant())
            .ToHashSet();
        if (accepted.Contains("br")) return "br";
        if (accepted.Contains("gzip")) return "gzip";
        return null;
    }
}

/// <summary>
/// 화장실별 평점·댓글 집계 (`GET /api/toilets/stats`). 평점이나 댓글이 하나라도 있는 곳만.
/// 전국 목록과 따로 두는 것은 자주 바뀌어서: 같이 두면 평점 하나에 전국 목록을 다시 받아야 한다.
/// 형식: <c>{ fields, rows }</c>, 행은 <c>[id, 평균 별점|null, 평점 수, 댓글 수, 도움돼요, 아니에요]</c>.
/// 도움돼요·아니에요는 가려지지 않은 댓글이 받은 표 (추천 점수의 "믿음", <see cref="ToiletRecommender"/>).
/// </summary>
public sealed class ToiletStatsProvider(IServiceScopeFactory scopes, TimeProvider time)
{
    public static readonly TimeSpan MaxAge = TimeSpan.FromMinutes(1);

    public static readonly string[] Fields = ["id", "averageRating", "ratingCount", "commentCount", "helpfulUp", "helpfulDown"];

    public sealed record StatsRow(long Id, double? AverageRating, int RatingCount, int CommentCount, int HelpfulUp, int HelpfulDown);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private (IReadOnlyList<StatsRow> Rows, DateTimeOffset At)? _current;

    public async Task<IReadOnlyList<StatsRow>> GetAsync(CancellationToken ct)
    {
        if (_current is { } c && time.GetUtcNow() - c.At < MaxAge) return c.Rows;
        await _gate.WaitAsync(ct);
        try
        {
            if (_current is { } c2 && time.GetUtcNow() - c2.At < MaxAge) return c2.Rows;
            using var scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MashilDbContext>();
            var ratings = await db.Ratings.AsNoTracking()
                .GroupBy(r => r.ToiletId)
                .Select(g => new { Id = g.Key, Avg = g.Average(r => (double)r.Score), Count = g.Count() })
                .ToDictionaryAsync(x => x.Id, ct);
            var comments = await db.Comments.AsNoTracking()
                .Where(x => !x.Hidden)
                .GroupBy(x => x.ToiletId)
                .Select(g => new { Id = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
            var votes = await db.CommentVotes.AsNoTracking()
                .Join(db.Comments, v => v.CommentId, cm => cm.Id, (v, cm) => new { v.Value, cm.ToiletId, cm.Hidden })
                .Where(x => !x.Hidden)
                .GroupBy(x => x.ToiletId)
                .Select(g => new { Id = g.Key, Up = g.Count(x => x.Value > 0), Down = g.Count(x => x.Value < 0) })
                .ToDictionaryAsync(x => x.Id, ct);
            var ids = ratings.Keys.Concat(comments.Keys).Distinct().ToList();
            var visible = await db.Toilets.AsNoTracking()
                .Where(ToiletEndpoints.IsVisible)
                .Where(t => ids.Contains(t.Id))
                .Select(t => t.Id)
                .ToListAsync(ct);
            var rows = visible.Order().Select(id =>
            {
                var r = ratings.GetValueOrDefault(id);
                var v = votes.GetValueOrDefault(id);
                return new StatsRow(id, r is null ? null : Math.Round(r.Avg, 2), r?.Count ?? 0,
                    comments.GetValueOrDefault(id), v?.Up ?? 0, v?.Down ?? 0);
            }).ToList();
            _current = (rows, time.GetUtcNow());
            return rows;
        }
        finally
        {
            _gate.Release();
        }
    }
}

/// <summary>서버가 뜰 때 전국 목록을 미리 만든다 (첫 요청이 수 초 기다리지 않게). 실패해도 첫 요청 때 다시 만든다.</summary>
public sealed class ToiletSnapshotWarmup(ToiletSnapshotProvider snapshots, ILogger<ToiletSnapshotWarmup> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await snapshots.GetAsync(stoppingToken); }
        catch (OperationCanceledException) { }
        catch (Exception e) { logger.LogWarning(e, "Toilet snapshot warm-up failed; it will be built on the first request"); }
    }
}
