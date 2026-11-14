using System.Security.Claims;
using Mashil.Infrastructure.Identity;

namespace Mashil.Api.Auth;

/// <summary>
/// 약관 동의 전에는 쓰기를 막는다 (403 <c>consent_required</c>). <c>Consents:EnforceOnWrite = false</c> 면 통과.
/// 로그인 응답의 <c>consentRequired</c> 를 클라이언트가 무시해도 서버에서 보장하기 위한 것.
/// </summary>
public sealed class RequireConsentFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var consents = http.RequestServices.GetRequiredService<ConsentService>();
        if (consents.EnforceOnWrite && http.User.TryGetUserId() is { } userId
            && await consents.IsRequiredAsync(userId, http.RequestAborted))
        {
            return Results.Json(new { error = "consent_required" }, statusCode: StatusCodes.Status403Forbidden);
        }
        return await next(context);
    }
}

public static class RequireConsentExtensions
{
    public static RouteHandlerBuilder RequireConsent(this RouteHandlerBuilder builder) =>
        builder.AddEndpointFilter<RequireConsentFilter>();
}
