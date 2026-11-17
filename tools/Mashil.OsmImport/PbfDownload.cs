using System.Net.Http.Headers;

namespace Mashil.OsmImport;

/// <summary>
/// PBF 와 같은 이름의 .md5 를 받는다 (Geofabrik 은 매일 갱신, 한국 약 300MB). https 만.
/// .md5 를 못 받으면 멈춘다: 받은 파일을 확인할 수 없으면 도로망을 바꾸지 않는다.
/// </summary>
public static class PbfDownload
{
    public const string DefaultUrl = "https://download.geofabrik.de/asia/south-korea-latest.osm.pbf";

    public static async Task<string> FetchAsync(Uri url, string dir, Action<string> log, CancellationToken ct = default)
    {
        Validate(url);
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) };
        // 받는 쪽을 밝힌다 (Geofabrik 이용 안내). 개인 정보는 넣지 않는다
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("mashil-osmimport", "1.0"));

        var name = Path.GetFileName(url.AbsolutePath);
        var pbf = Path.Combine(dir, name);
        var md5 = await http.GetStringAsync(new Uri(url + ".md5"), ct);
        await File.WriteAllTextAsync(pbf + ".md5", md5, ct);

        using (var res = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            res.EnsureSuccessStatusCode();
            await using var src = await res.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(pbf);
            await src.CopyToAsync(dst, ct);
        }
        log($"받음 {url} ({new FileInfo(pbf).Length / 1024 / 1024:N0} MB)");
        return pbf;
    }

    /// <summary>https 이고 파일 이름이 .osm.pbf 로 끝나야 한다.</summary>
    public static void Validate(Uri url)
    {
        if (!url.IsAbsoluteUri || url.Scheme != Uri.UriSchemeHttps)
            throw new ArgumentException("https 주소만 받습니다");
        if (!url.AbsolutePath.EndsWith(".osm.pbf", StringComparison.OrdinalIgnoreCase) || !string.IsNullOrEmpty(url.Query))
            throw new ArgumentException(".osm.pbf 파일 주소가 아닙니다");
    }
}
