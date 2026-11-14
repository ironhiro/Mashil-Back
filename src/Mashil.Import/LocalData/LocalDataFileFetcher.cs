using System.Net;
using System.Net.Http.Headers;
using Mashil.Domain;

namespace Mashil.Import.LocalData;

/// <summary>설정 섹션 <c>Import:LocalData</c>.</summary>
public sealed record LocalDataFetchOptions
{
    /// <summary>기본 UA. 서비스를 밝힌다.</summary>
    public string UserAgent { get; init; } = "MashilDataSync/1.0";

    /// <summary>
    /// 정보 페이지 요청의 Referer. LOCALDATA 정보 페이지는 직접 접속하면 쿠키가 없어 302(→ error.html)이고,
    /// 공공데이터포털의 표준데이터 페이지에서 넘어와야 열린다 (2026-11-14, 브라우저 시크릿 창으로 확인).
    /// null 이면 Referer 없이 요청.
    /// </summary>
    public string? EntryReferer { get; init; } = "https://www.data.go.kr/data/15012892/standard.do";

    /// <summary>
    /// true 면 브라우저와 같은 헤더(UA, Accept, Sec-Fetch-*, sec-ch-ua)로 요청한다.
    /// 2026-11-14 기준으로는 <see cref="EntryReferer"/> 만으로 받아져서 불필요 — 기본 false.
    /// 사이트 조건이 바뀌어 다시 막힐 때를 대비해 남겨 둔다.
    /// </summary>
    public bool BrowserHeaders { get; init; }

    public string BrowserUserAgent { get; init; } =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/130.0.0.0 Safari/537.36";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(3);

    /// <summary>네트워크·5xx 오류 재시도 간격. 길이 = 재시도 횟수.</summary>
    public IReadOnlyList<TimeSpan> TransientRetryDelays { get; init; } = [TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60)];

    /// <summary>download-count 가 429 일 때 대기 간격 (Retry-After 가 없을 때). 길이 = 재확인 횟수.</summary>
    public IReadOnlyList<TimeSpan> RateLimitDelays { get; init; } = [TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(60)];
}

/// <summary>
/// LOCALDATA 전국파일 다운로드 (DATA-19). 브라우저와 같은 순서로 요청한다:
/// <list type="number">
/// <item>정보 페이지 GET → 쿠키 <c>XSRF-TOKEN</c>, <c>WMONID</c> (실행마다 새로 받고 저장하지 않음)</item>
/// <item><c>GET /file/validate/download-count</c> — 200 허용 / 429 빈도 제한 / 그 외 오류</item>
/// <item><c>GET /file/download/...</c> — Referer 필수, 302(→ error.html)는 실패</item>
/// </list>
/// </summary>
public sealed class LocalDataFileFetcher(
    LocalDataFetchOptions? options = null,
    Func<CookieContainer, HttpMessageHandler>? handlerFactory = null,
    Func<TimeSpan, CancellationToken, Task>? delay = null)
{
    public const string ValidatePath = "/file/validate/download-count";

    private readonly LocalDataFetchOptions _opts = options ?? new LocalDataFetchOptions();
    private readonly Func<CookieContainer, HttpMessageHandler> _handlerFactory = handlerFactory ?? DefaultHandler;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;

    private static HttpMessageHandler DefaultHandler(CookieContainer cookies) => new SocketsHttpHandler
    {
        CookieContainer = cookies,
        UseCookies = true,
        AllowAutoRedirect = false, // 302 → /error.html 을 성공으로 오인하지 않게
        AutomaticDecompression = DecompressionMethods.All,
    };

    public async Task<SourceFile> FetchAsync(DataSource source, CancellationToken ct)
    {
        var info = new Uri(source.InfoUrl ?? throw new InvalidOperationException($"{source.Code}: InfoUrl 없음"));
        var download = new Uri(source.DownloadUrl ?? throw new InvalidOperationException($"{source.Code}: DownloadUrl 없음"));

        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await FetchOnceAsync(info, download, ct);
            }
            catch (Exception ex) when (IsTransient(ex, ct) && attempt < _opts.TransientRetryDelays.Count)
            {
                await _delay(_opts.TransientRetryDelays[attempt], ct);
            }
        }
    }

    private async Task<SourceFile> FetchOnceAsync(Uri info, Uri download, CancellationToken ct)
    {
        using var http = new HttpClient(_handlerFactory(new CookieContainer()), disposeHandler: true) { Timeout = _opts.Timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(_opts.BrowserHeaders ? _opts.BrowserUserAgent : _opts.UserAgent);

        var entry = string.IsNullOrWhiteSpace(_opts.EntryReferer) ? null : new Uri(_opts.EntryReferer);
        using (var page = await http.SendAsync(Get(info, referer: entry, RequestKind.Page), ct))
            EnsureOk(page, "정보 페이지");

        await WaitForQuotaAsync(http, new Uri(info, ValidatePath), info, ct);

        using var res = await http.SendAsync(Get(download, referer: info, RequestKind.Download), HttpCompletionOption.ResponseHeadersRead, ct);
        EnsureOk(res, "파일 다운로드");

        var bytes = await res.Content.ReadAsByteArrayAsync(ct);
        var expected = res.Content.Headers.ContentLength;
        if (expected is not null && expected != bytes.LongLength)
            throw new LocalDataDownloadException($"받은 크기 {bytes.LongLength:N0} ≠ Content-Length {expected:N0}", transient: true);

        if (LooksLikeHtml(bytes) || res.Content.Headers.ContentType?.MediaType == "text/html")
            throw new LocalDataDownloadException("CSV 대신 HTML 이 왔습니다 (오류 페이지로 보임)", transient: false);

        var fileName = res.Content.Headers.ContentDisposition?.FileNameStar
            ?? res.Content.Headers.ContentDisposition?.FileName?.Trim('"')
            ?? "공중화장실정보.csv";
        return new SourceFile(bytes, fileName);
    }

    private async Task WaitForQuotaAsync(HttpClient http, Uri validate, Uri referer, CancellationToken ct)
    {
        for (var i = 0; ; i++)
        {
            using var res = await http.SendAsync(Get(validate, referer, RequestKind.Xhr), ct);
            if (res.StatusCode == HttpStatusCode.OK) return;

            if (res.StatusCode == HttpStatusCode.TooManyRequests)
            {
                var message = (await res.Content.ReadAsStringAsync(ct)).Trim();
                if (message.Length == 0) message = "잠시후 시도해주세요.";
                if (i >= _opts.RateLimitDelays.Count)
                    throw new LocalDataRateLimitedException(message);

                var wait = res.Headers.RetryAfter?.Delta ?? _opts.RateLimitDelays[i];
                await _delay(wait, ct);
                continue;
            }

            EnsureOk(res, "다운로드 가능 여부 확인");
        }
    }

    private enum RequestKind { Page, Xhr, Download }

    private const string NavigateAccept =
        "text/html,application/xhtml+xml,application/xml;q=0.9,image/avif,image/webp,image/apng,*/*;q=0.8";

    private HttpRequestMessage Get(Uri uri, Uri? referer, RequestKind kind)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, uri);
        if (referer is not null) req.Headers.Referrer = referer;

        if (!_opts.BrowserHeaders)
        {
            req.Headers.Accept.ParseAdd(kind switch { RequestKind.Page => "text/html", RequestKind.Xhr => "*/*", _ => "text/csv,*/*;q=0.8" });
            req.Headers.AcceptLanguage.Add(new StringWithQualityHeaderValue("ko-KR"));
            return req;
        }

        // 브라우저(Chromium)가 보내는 것과 같은 구성. 사이트가 실제로 받는 요청 순서: 페이지 이동 → XHR → iframe 다운로드
        req.Headers.TryAddWithoutValidation("Accept", kind == RequestKind.Xhr ? "*/*" : NavigateAccept);
        req.Headers.TryAddWithoutValidation("Accept-Language", "ko-KR,ko;q=0.9,en-US;q=0.8,en;q=0.7");
        req.Headers.TryAddWithoutValidation("sec-ch-ua", "\"Chromium\";v=\"130\", \"Google Chrome\";v=\"130\", \"Not?A_Brand\";v=\"99\"");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-mobile", "?0");
        req.Headers.TryAddWithoutValidation("sec-ch-ua-platform", "\"Windows\"");
        switch (kind)
        {
            case RequestKind.Page:
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "document");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", referer is null ? "none" : "cross-site");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
                req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
                break;
            case RequestKind.Xhr:
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "empty");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "cors");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
                break;
            case RequestKind.Download:
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Dest", "iframe");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Mode", "navigate");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
                req.Headers.TryAddWithoutValidation("Sec-Fetch-User", "?1");
                req.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");
                break;
        }
        return req;
    }

    private static void EnsureOk(HttpResponseMessage res, string step)
    {
        if (res.IsSuccessStatusCode) return;
        var location = res.Headers.Location is { } l ? $" → {l}" : "";
        var transient = (int)res.StatusCode >= 500 || res.StatusCode == HttpStatusCode.RequestTimeout;
        throw new LocalDataDownloadException($"{step} 실패: HTTP {(int)res.StatusCode}{location}", transient);
    }

    private static bool LooksLikeHtml(byte[] bytes)
    {
        foreach (var b in bytes.AsSpan(0, Math.Min(bytes.Length, 64)))
        {
            if (b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or 0xEF or 0xBB or 0xBF) continue;
            return b == (byte)'<';
        }
        return false;
    }

    private static bool IsTransient(Exception ex, CancellationToken ct) => ex switch
    {
        LocalDataDownloadException d => d.Transient,
        HttpRequestException => true,
        TaskCanceledException when !ct.IsCancellationRequested => true, // 타임아웃
        _ => false,
    };
}

public class LocalDataDownloadException(string message, bool transient) : Exception(message)
{
    public bool Transient { get; } = transient;
}

public sealed class LocalDataRateLimitedException(string serverMessage)
    : LocalDataDownloadException($"요청 빈도 제한(429): {serverMessage}", transient: false);
