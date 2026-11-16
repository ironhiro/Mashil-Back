using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Net.Http.Headers;

namespace Mashil.Api.Ops;

/// <summary>
/// 웹(SPA) 정적 파일을 API 와 같은 도메인에서 내보낸다 (Azure Container Apps: 이미지 하나에 API + 웹 빌드).
/// VM 구성에서 Caddy 가 하던 일: 보안 헤더, 캐시 정책, SPA 경로를 index.html 로.
/// <c>wwwroot/index.html</c> 이 없으면(로컬 개발: Vite 가 웹을 띄움) 아무것도 하지 않는다. 보안 헤더는 늘 붙인다.
/// </summary>
public static class WebHosting
{
    /// <summary>모든 응답에 붙이는 보안 헤더 (NFR-05, VM 의 Caddyfile 과 같은 값).</summary>
    public static IApplicationBuilder UseMashilSecurityHeaders(this IApplicationBuilder app) =>
        app.Use((ctx, next) =>
        {
            ctx.Response.OnStarting(() =>
            {
                var h = ctx.Response.Headers;
                // HSTS 는 1년. 하위 도메인 정책은 따로 정하지 않아 includeSubDomains 는 넣지 않는다
                h["Strict-Transport-Security"] = "max-age=31536000";
                h["X-Content-Type-Options"] = "nosniff";
                h["Referrer-Policy"] = "strict-origin-when-cross-origin";
                h["X-Frame-Options"] = "SAMEORIGIN";
                h["Permissions-Policy"] = "camera=(), microphone=(), payment=(), usb=(), geolocation=(self)";
                h.Remove("Server");
                return Task.CompletedTask;
            });
            return next();
        });

    /// <summary>정적 파일과 SPA 대체 경로. 엔드포인트를 다 매핑한 뒤에 부른다.</summary>
    public static WebApplication MapMashilWeb(this WebApplication app)
    {
        var index = app.Environment.WebRootFileProvider.GetFileInfo("index.html");
        if (!index.Exists) return app;

        app.UseStaticFiles(new StaticFileOptions
        {
            ContentTypeProvider = new FileExtensionContentTypeProvider(),
            OnPrepareResponse = c =>
            {
                // 빌드 해시가 붙은 에셋은 바뀌지 않으므로 1년, 나머지(index.html, public/ 파일)는 매번 재검증
                c.Context.Response.Headers[HeaderNames.CacheControl] =
                    c.Context.Request.Path.StartsWithSegments("/assets")
                        ? "public, max-age=31536000, immutable"
                        : "no-cache";
            },
        });

        // 웹 화면 경로(/toilets/12, /me …)는 index.html. API·헬스체크·파일(확장자 있는 경로)은 대체하지 않고 404
        app.MapFallback(async ctx =>
        {
            var path = ctx.Request.Path;
            if (path.StartsWithSegments("/api") || path.StartsWithSegments("/healthz") || Path.HasExtension(path.Value)
                || !(HttpMethods.IsGet(ctx.Request.Method) || HttpMethods.IsHead(ctx.Request.Method)))
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }
            ctx.Response.ContentType = "text/html; charset=utf-8";
            ctx.Response.Headers[HeaderNames.CacheControl] = "no-cache";
            await ctx.Response.SendFileAsync(index);
        });
        return app;
    }
}
