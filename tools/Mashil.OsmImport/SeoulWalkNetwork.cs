using System.Globalization;
using System.Text.Json;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;

namespace Mashil.OsmImport;

/// <summary>서울시 도보 네트워크의 링크 하나 (노드 id 는 서울시 id).</summary>
public sealed record SeoulLink(long Id, long From, long To, LineString Geom, double LengthM, bool Crosswalk, bool Overpass, bool Tunnel, bool Subway);

/// <summary>
/// 서울시 자치구별 도보 네트워크 공간정보 (서울 열린데이터광장 Open API <c>TbTraficWlkNet</c>, 공공누리 1유형: 출처 표시).
/// 보행자용으로 만든 노드·링크라 대로변 횡단보도, 육교, 지하철 출입구가 이어져 있다. 매일 갱신.
/// <para>
/// 보안: 이 API 는 HTTP(8088) 만 받아 인증키가 암호화 없이 오간다. 키는 사용자 비밀/환경 변수에서만 읽고,
/// 로그와 오류 메시지에는 가린 주소만 남긴다. 키가 새면 열린데이터광장에서 재발급한다.
/// </para>
/// </summary>
public static class SeoulWalkNetwork
{
    private const string Service = "TbTraficWlkNet";
    private const int PageSize = 1000; // API 한 번에 최대 1000 행

    public static async Task<List<SeoulLink>> FetchAsync(string apiKey, Action<string> log, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(apiKey) || apiKey.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new InvalidOperationException("SeoulOpenData:ApiKey 가 비었거나 형식이 다릅니다.");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MashilOsmImport/1.0");

        var first = await GetPageAsync(http, apiKey, 1, 1, ct);
        var total = first.Total;
        if (total is <= 0 or > 5_000_000) throw new InvalidOperationException($"전체 건수가 이상합니다: {total}");
        log($"서울 도보 네트워크 {total:N0}행 받는 중");

        var starts = Enumerable.Range(0, (total + PageSize - 1) / PageSize).Select(i => i * PageSize + 1).ToList();
        var pages = new List<(int Start, List<SeoulLink> Links)>();
        var gate = new SemaphoreSlim(4); // 동시에 4개까지만 (상대 서버를 배려)
        var done = 0;
        await Task.WhenAll(starts.Select(async s =>
        {
            await gate.WaitAsync(ct);
            try
            {
                var page = await GetPageAsync(http, apiKey, s, Math.Min(s + PageSize - 1, total), ct);
                lock (pages) pages.Add((s, page.Links));
                if (Interlocked.Increment(ref done) % 50 == 0) log($"  {done}/{starts.Count} 쪽");
            }
            finally
            {
                gate.Release();
            }
        }));
        return pages.OrderBy(p => p.Start).SelectMany(p => p.Links).ToList();
    }

    private static async Task<(int Total, List<SeoulLink> Links)> GetPageAsync(HttpClient http, string key, int from, int to, CancellationToken ct)
    {
        var url = $"http://openapi.seoul.go.kr:8088/{key}/json/{Service}/{from}/{to}/";
        var masked = $"http://openapi.seoul.go.kr:8088/****/json/{Service}/{from}/{to}/";
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var res = await http.GetAsync(url, ct);
                res.EnsureSuccessStatusCode();
                await using var body = await res.Content.ReadAsStreamAsync(ct);
                using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
                if (!doc.RootElement.TryGetProperty(Service, out var root))
                {
                    var code = doc.RootElement.TryGetProperty("RESULT", out var r) && r.TryGetProperty("CODE", out var c) ? c.GetString() : "?";
                    throw new InvalidOperationException($"API 응답 오류 {code} ({masked})");
                }
                var total = root.GetProperty("list_total_count").GetInt32();
                var links = new List<SeoulLink>();
                if (root.TryGetProperty("row", out var rows))
                    foreach (var row in rows.EnumerateArray())
                        if (Parse(row) is { } link) links.Add(link);
                return (total, links);
            }
            catch (Exception ex) when (attempt < 4 && ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
            {
                // 예외 메시지에 키가 든 주소가 섞이지 않게 가린 주소로 바꿔 던진다
                throw new InvalidOperationException($"서울 도보 네트워크를 받지 못했습니다 ({masked}): {ex.GetType().Name}");
            }
        }
    }

    private static readonly WKTReader Wkt = new(new NetTopologySuite.NtsGeometryServices(new PrecisionModel(), 4326));

    /// <summary>링크 행만 (노드 행과 자동차 전용 도로 링크는 뺀다). 형식이 이상한 행은 버린다.</summary>
    private static SeoulLink? Parse(JsonElement row)
    {
        if (Str(row, "NODE_TYPE") != "LINK") return null;
        if (Str(row, "EXPN_CAR_RD") == "1") return null;
        var wkt = Str(row, "LNKG_WKT");
        if (string.IsNullOrEmpty(wkt) || !wkt.StartsWith("LINESTRING", StringComparison.Ordinal) || wkt.Length > 200_000) return null;
        LineString line;
        try
        {
            line = (LineString)Wkt.Read(wkt);
        }
        catch (Exception)
        {
            return null;
        }
        if (line.NumPoints < 2 || line.Coordinates.Any(c => c.X is < 126.5 or > 127.5 || c.Y is < 37.3 or > 37.8)) return null;
        var from = (long)Num(row, "BGNG_LNKG_ID");
        var to = (long)Num(row, "END_LNKG_ID");
        if (from <= 0 || to <= 0) return null;
        var len = Num(row, "LNKG_LEN");
        return new SeoulLink((long)Num(row, "LNKG_ID"), from, to, line, len > 0 ? len : 0,
            Str(row, "CRSWK") == "1", Str(row, "OVRP") == "1", Str(row, "TNL") == "1", Str(row, "SBWY_NTW") == "1");
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetDouble().ToString(CultureInfo.InvariantCulture),
            _ => "",
        } : "";

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.Number => v.GetDouble(),
            JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
            _ => 0,
        } : 0;
}
