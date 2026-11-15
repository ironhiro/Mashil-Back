using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Mashil.Domain;
using Mashil.Import.LocalData;

namespace Mashil.Import.Tests;

public class LocalDataFileFetcherTests
{
    private static readonly DataSource Source = new()
    {
        Code = DataSourceCodes.LocalDataPublicRestroom, Name = "t", Provider = "LOCALDATA", Format = "CSV",
        InfoUrl = "https://file.localdata.go.kr/file/public_restroom_info/info",
        DownloadUrl = "https://file.localdata.go.kr/file/download/public_restroom_info/info",
    };

    private sealed class FakeHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            var sameSoFar = Requests.Count(r => r.RequestUri!.AbsolutePath == request.RequestUri!.AbsolutePath);
            return Task.FromResult(respond(request, sameSoFar));
        }
    }

    private static readonly byte[] Csv = TestCsv.Cp949(TestCsv.Header, TestCsv.Row1);

    private static HttpResponseMessage CsvResponse(byte[]? body = null, long? declaredLength = null)
    {
        var content = new ByteArrayContent(body ?? Csv);
        content.Headers.ContentType = new MediaTypeHeaderValue("text/csv") { CharSet = "UTF-8" }; // 실제 서버처럼 틀린 charset
        content.Headers.ContentDisposition = new ContentDispositionHeaderValue("attachment") { FileNameStar = "공중화장실정보.csv" };
        content.Headers.ContentLength = declaredLength ?? (body ?? Csv).LongLength;
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = content };
    }

    private static (LocalDataFileFetcher fetcher, FakeHandler handler, List<TimeSpan> waits) Create(Func<HttpRequestMessage, int, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        var waits = new List<TimeSpan>();
        var fetcher = new LocalDataFileFetcher(new LocalDataFetchOptions(), _ => handler, (t, _) => { waits.Add(t); return Task.CompletedTask; });
        return (fetcher, handler, waits);
    }

    private static HttpResponseMessage Ok(string body = "") => new(HttpStatusCode.OK) { Content = new StringContent(body) };

    [Fact]
    public async Task Happy_path_requests_in_browser_order_with_referer_and_detects_cp949()
    {
        var (fetcher, handler, _) = Create((req, _) => req.RequestUri!.AbsolutePath switch
        {
            "/file/public_restroom_info/info" => Ok("<html/>"),
            "/file/validate/download-count" => Ok(),
            _ => CsvResponse(),
        });

        var file = await fetcher.FetchAsync(Source, CancellationToken.None);

        Assert.Equal(["/file/public_restroom_info/info", "/file/validate/download-count", "/file/download/public_restroom_info/info"],
            handler.Requests.Select(r => r.RequestUri!.AbsolutePath));
        Assert.Equal("https://www.data.go.kr/data/15012892/standard.do", handler.Requests[0].Headers.Referrer?.ToString()); // 포털에서 진입
        Assert.Equal(Source.InfoUrl, handler.Requests[1].Headers.Referrer?.ToString());
        Assert.Equal(Source.InfoUrl, handler.Requests[2].Headers.Referrer?.ToString());
        Assert.Contains("MashilDataSync", handler.Requests[2].Headers.UserAgent.ToString());
        Assert.Equal("공중화장실정보.csv", file.FileName);
        Assert.Equal(DetectedEncoding.Cp949, file.Encoding); // 헤더의 UTF-8 선언을 믿지 않음
    }

    [Fact]
    public async Task Browser_headers_mode_sends_navigation_and_xhr_headers_per_step()
    {
        var handler = new FakeHandler((req, _) => req.RequestUri!.AbsolutePath.StartsWith("/file/download/") ? CsvResponse() : Ok());
        var fetcher = new LocalDataFileFetcher(new LocalDataFetchOptions { BrowserHeaders = true }, _ => handler, (_, _) => Task.CompletedTask);

        await fetcher.FetchAsync(Source, CancellationToken.None);

        string H(int i, string name) => handler.Requests[i].Headers.TryGetValues(name, out var v) ? string.Join(",", v) : "";
        Assert.StartsWith("Mozilla/5.0", handler.Requests[0].Headers.UserAgent.ToString());
        Assert.Equal(("document", "navigate", "cross-site"), (H(0, "Sec-Fetch-Dest"), H(0, "Sec-Fetch-Mode"), H(0, "Sec-Fetch-Site")));
        Assert.Equal(("empty", "cors", "same-origin"), (H(1, "Sec-Fetch-Dest"), H(1, "Sec-Fetch-Mode"), H(1, "Sec-Fetch-Site")));
        Assert.Equal(("iframe", "navigate", "same-origin"), (H(2, "Sec-Fetch-Dest"), H(2, "Sec-Fetch-Mode"), H(2, "Sec-Fetch-Site")));
        Assert.StartsWith("text/html", H(2, "Accept"));
        Assert.Equal(Source.InfoUrl, handler.Requests[2].Headers.Referrer?.ToString());
    }

    [Fact]
    public async Task Default_mode_does_not_send_browser_headers()
    {
        var (fetcher, handler, _) = Create((req, _) => req.RequestUri!.AbsolutePath.StartsWith("/file/download/") ? CsvResponse() : Ok());
        await fetcher.FetchAsync(Source, CancellationToken.None);
        Assert.All(handler.Requests, r => Assert.False(r.Headers.Contains("Sec-Fetch-Mode")));
    }

    [Fact]
    public async Task Rate_limited_then_allowed_waits_and_retries()
    {
        var (fetcher, _, waits) = Create((req, n) => req.RequestUri!.AbsolutePath switch
        {
            "/file/validate/download-count" when n == 1 => new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("잠시후 시도해주세요.") },
            "/file/validate/download-count" => Ok(),
            "/file/download/public_restroom_info/info" => CsvResponse(),
            _ => Ok(),
        });

        await fetcher.FetchAsync(Source, CancellationToken.None);
        Assert.Equal([TimeSpan.FromMinutes(15)], waits);
    }

    [Fact]
    public async Task Rate_limited_every_time_gives_up_without_downloading()
    {
        var (fetcher, handler, waits) = Create((req, _) => req.RequestUri!.AbsolutePath == LocalDataFileFetcher.ValidatePath
            ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("") }
            : Ok());

        var ex = await Assert.ThrowsAsync<LocalDataRateLimitedException>(() => fetcher.FetchAsync(Source, CancellationToken.None));
        Assert.Contains("잠시후 시도해주세요.", ex.Message);
        Assert.Equal(3, waits.Count);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.AbsolutePath.StartsWith("/file/download/"));
    }

    [Fact]
    public async Task Redirect_to_error_page_fails_without_retry()
    {
        var (fetcher, handler, waits) = Create((req, _) => req.RequestUri!.AbsolutePath.StartsWith("/file/download/")
            ? new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri("/error.html", UriKind.Relative) } }
            : Ok());

        var ex = await Assert.ThrowsAsync<LocalDataDownloadException>(() => fetcher.FetchAsync(Source, CancellationToken.None));
        Assert.Contains("302", ex.Message);
        Assert.Contains("error.html", ex.Message);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Html_body_is_rejected()
    {
        var (fetcher, _, _) = Create((req, _) => req.RequestUri!.AbsolutePath.StartsWith("/file/download/")
            ? CsvResponse(Encoding.UTF8.GetBytes("\n<!DOCTYPE html><html>오류</html>"))
            : Ok());

        var ex = await Assert.ThrowsAsync<LocalDataDownloadException>(() => fetcher.FetchAsync(Source, CancellationToken.None));
        Assert.Contains("HTML", ex.Message);
    }

    [Fact]
    public async Task Server_error_is_retried_then_succeeds()
    {
        var (fetcher, _, waits) = Create((req, n) => req.RequestUri!.AbsolutePath switch
        {
            "/file/download/public_restroom_info/info" when n == 1 => new HttpResponseMessage(HttpStatusCode.BadGateway),
            "/file/download/public_restroom_info/info" => CsvResponse(),
            _ => Ok(),
        });

        var file = await fetcher.FetchAsync(Source, CancellationToken.None);
        Assert.Single(waits);
        Assert.Equal(Csv.LongLength, file.Bytes.LongLength);
    }
}
