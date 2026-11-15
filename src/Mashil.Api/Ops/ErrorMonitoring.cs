using System.Text.RegularExpressions;
using Sentry;
using Sentry.Extensibility;
using Sentry.Serilog;
using Serilog;
using Serilog.Events;

namespace Mashil.Api.Ops;

/// <summary>
/// 에러 모니터링 (NFR-10). <c>Sentry:Dsn</c>(환경 변수 <c>Sentry__Dsn</c>) 이 있을 때만 켜진다.
/// 개인정보는 보내지 않는다: SendDefaultPii=false(IP·사용자·쿠키·헤더 없음), 요청 본문 없음,
/// URL·쿼리·브레드크럼의 위치 파라미터(lat/lng, bbox)는 지운다 (NFR-06).
/// 나머지 Sentry 설정(Environment, Release, TracesSampleRate 등)은 <c>Sentry</c> 섹션에서 SDK 가 직접 읽는다.
/// </summary>
public static class ErrorMonitoring
{
    /// <summary>DSN 이 있고 모양이 맞을 때만 켠다. 잘못 적은 DSN 때문에 API 가 뜨지 못하는 일은 없게 한다.</summary>
    public static bool IsEnabled(IConfiguration config) => IsValidDsn(config["Sentry:Dsn"]);

    public static bool IsValidDsn(string? dsn) =>
        !string.IsNullOrWhiteSpace(dsn)
        && Uri.TryCreate(dsn.Trim(), UriKind.Absolute, out var u)
        && u.Scheme is "https" or "http" && !string.IsNullOrEmpty(u.UserInfo) && u.AbsolutePath.Trim('/').Length > 0;

    /// <summary>
    /// 통신에 끼어들지 않게 하는 설정 (API·Worker 공통). Sentry 는 기본으로 나가는 모든 HTTP 요청에 sentry-trace·baggage 헤더를
    /// 붙이는데, 그 헤더를 허용하지 않는 서버(CORS 사전 요청, 엄격한 외부 API)와의 통신이 깨진 적이 있다.
    /// 마실은 오류 수집만 쓰고 분산 추적은 쓰지 않으므로 헤더 전파와 HttpClient 가로채기를 끈다.
    /// 외부 호출: Kakao·Google·Apple 로그인 검증, Kakao 지오코딩, LOCALDATA 다운로드.
    /// </summary>
    public static void KeepOutOfTraffic(SentryOptions o)
    {
        o.TracePropagationTargets = [];
        o.PropagateTraceparent = false;
        o.DisableSentryHttpMessageHandler = true;
        o.TracesSampleRate = 0;
    }

    public static IWebHostBuilder UseMashilSentry(this IWebHostBuilder web, IConfiguration config)
    {
        if (!IsEnabled(config)) return web;
        return web.UseSentry(o =>
        {
            o.SendDefaultPii = false;
            o.MaxRequestBodySize = RequestSize.None;
            KeepOutOfTraffic(o);
            o.AutoRegisterTracing = false; // 추적 미들웨어도 넣지 않는다 (요청 처리 경로에 Sentry 가 끼지 않게)
            o.SetBeforeSend((e, _) => { ScrubRequest(e.Request); return e; });
            o.SetBeforeSendTransaction((t, _) => { ScrubRequest(t.Request); return t; });
            o.SetBeforeBreadcrumb((b, _) => LocationScrubber.Scrub(b));
            o.UseSerilog(); // Serilog 싱크(WriteToSentryIfEnabled)가 이 SDK 로 보낸다 (Sentry 7: 싱크는 SDK 를 초기화하지 않음)
        });
    }

    /// <summary>Serilog 의 Error 이상을 Sentry 이벤트로, Information 이상을 브레드크럼으로 (SDK 초기화는 <see cref="UseMashilSentry"/> 가 한다).</summary>
    public static LoggerConfiguration WriteToSentryIfEnabled(this LoggerConfiguration lc, IConfiguration config) =>
        IsEnabled(config)
            ? lc.WriteTo.Sentry(o =>
            {
                o.MinimumEventLevel = LogEventLevel.Error;
                o.MinimumBreadcrumbLevel = LogEventLevel.Information;
            })
            : lc;

    private static void ScrubRequest(SentryRequest? r)
    {
        if (r is null) return;
        r.QueryString = LocationScrubber.ScrubQuery(r.QueryString);
        r.Url = LocationScrubber.ScrubUrl(r.Url);
        r.Cookies = null;
        r.Data = null;
    }
}

/// <summary>사용자 위치가 담기는 쿼리 파라미터를 지운다. Caddy 접근 로그 필터(deploy/Caddyfile)와 같은 목록.</summary>
public static partial class LocationScrubber
{
    public static readonly string[] Keys = ["lat", "lng", "north", "south", "east", "west"];

    [GeneratedRegex(@"(?<=[?&])(lat|lng|north|south|east|west)=[^&#\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LocationParam();

    /// <summary><c>?a=1&amp;lat=37.5&amp;lng=127</c> → <c>?a=1&amp;lat=[Filtered]&amp;lng=[Filtered]</c>. 앞의 ? 는 있어도 없어도 된다.</summary>
    public static string? ScrubQuery(string? query)
    {
        if (string.IsNullOrEmpty(query)) return query;
        var hasMark = query[0] == '?';
        var scrubbed = LocationParam().Replace(hasMark ? query : "?" + query, m => m.Groups[1].Value + "=[Filtered]");
        return hasMark ? scrubbed : scrubbed[1..];
    }

    public static string? ScrubUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url;
        var q = url.IndexOf('?');
        return q < 0 ? url : url[..q] + ScrubQuery(url[q..]);
    }

    /// <summary>HTTP 브레드크럼(url 데이터)과 메시지 안의 위치 파라미터를 지운 새 브레드크럼.</summary>
    public static Breadcrumb Scrub(Breadcrumb b)
    {
        var message = ScrubUrlsInText(b.Message);
        Dictionary<string, string>? data = null;
        if (b.Data is { Count: > 0 })
            data = b.Data.ToDictionary(kv => kv.Key, kv => ScrubUrlsInText(kv.Value) ?? "");
        if (message == b.Message && (data is null || data.All(kv => b.Data![kv.Key] == kv.Value))) return b;
        return new Breadcrumb(message ?? "", b.Type ?? "default", data, b.Category, b.Level);
    }

    private static string? ScrubUrlsInText(string? text) =>
        string.IsNullOrEmpty(text) || text.IndexOf('=') < 0
            ? text
            : LocationParam().Replace(text, m => m.Groups[1].Value + "=[Filtered]");
}
