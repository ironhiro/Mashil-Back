using System.Security.Claims;
using System.Text.Json;
using Mashil.Api.Auth;
using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Api.Endpoints;

/// <summary>
/// 내 활동: 내가 쓴 댓글, 내가 보낸 제보와 그 진행 상황. 앱의 "내 정보" 탭이 쓴다.
/// 사용자 ID 는 JWT sub claim 에서만 읽는다 (다른 사람 것을 볼 수 없음).
/// </summary>
public static class MeEndpoints
{
    public const int MaxRows = 100;

    public static void MapMeEndpoints(this IEndpointRouteBuilder app)
    {
        var me = app.MapGroup("/api/me").WithTags("Me").RequireAuthorization();

        me.MapGet("/comments", async (ClaimsPrincipal principal, MashilDbContext db, CancellationToken ct) =>
        {
            var userId = principal.GetUserIdOrThrow();
            var rows = await db.Comments
                .Where(c => c.UserId == userId)
                .OrderByDescending(c => c.CreatedAt)
                .Take(MaxRows)
                .GroupJoin(db.Toilets, c => c.ToiletId, t => t.Id, (c, ts) => new { c, ts })
                .SelectMany(x => x.ts.DefaultIfEmpty(), (x, t) => new MyComment(
                    x.c.Id, x.c.ToiletId, t != null ? t.Name : null, x.c.Text, x.c.Hidden, x.c.CreatedAt, x.c.UpdatedAt))
                .ToListAsync(ct);
            return Results.Ok(rows.OrderByDescending(r => r.CreatedAt));
        })
        .WithSummary("내가 쓴 댓글 (인증 필요)")
        .WithDescription("최신순 100건. 신고로 가려진 댓글도 `hidden = true` 로 포함한다 (본인에게는 왜 안 보이는지 알려 주기 위해). 화장실이 지워졌으면 `toiletName = null`.")
        .Produces<IReadOnlyList<MyComment>>()
        .Produces(StatusCodes.Status401Unauthorized);

        me.MapGet("/change-requests", async (ClaimsPrincipal principal, MashilDbContext db, CancellationToken ct) =>
        {
            var userId = principal.GetUserIdOrThrow();
            var rows = await db.ChangeRequests
                .Where(c => c.RequesterId == userId)
                .OrderByDescending(c => c.CreatedAt)
                .Take(MaxRows)
                .GroupJoin(db.Toilets, c => c.TargetToiletId, t => (long?)t.Id, (c, ts) => new { c, ts })
                .SelectMany(x => x.ts.DefaultIfEmpty(), (x, t) => new { x.c, ToiletName = t != null ? t.Name : null })
                .ToListAsync(ct);
            return Results.Ok(rows
                .OrderByDescending(r => r.c.CreatedAt)
                .Select(r => new MyChangeRequest(
                    r.c.Id, r.c.Kind, r.c.Status, r.c.TargetToiletId,
                    // 추가 요청은 대상이 없으니 제보에 적힌 이름을 보여 준다
                    r.ToiletName ?? PayloadName(r.c.PayloadJson),
                    r.c.CreatedAt, r.c.ReviewedAt, r.c.ReviewNote)));
        })
        .WithSummary("내가 보낸 제보와 진행 상황 (인증 필요)")
        .WithDescription("최신순 100건. `status` 는 `Pending`(검토 중) / `Approved`(반영됨) / `Rejected`(반영 안 됨). 검토 메모(`reviewNote`)가 있으면 함께 준다.")
        .Produces<IReadOnlyList<MyChangeRequest>>()
        .Produces(StatusCodes.Status401Unauthorized);
    }

    internal static string? PayloadName(string payloadJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("name", out var n)
                && n.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(n.GetString())
                ? n.GetString()!.Trim()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>내 댓글 한 건.</summary>
    public sealed record MyComment(
        long Id, long ToiletId, string? ToiletName, string Text, bool Hidden, DateTime CreatedAt, DateTime UpdatedAt);

    /// <summary>내 제보 한 건. `toiletName` 은 대상 화장실 이름, 추가 요청이면 제보에 적힌 이름.</summary>
    public sealed record MyChangeRequest(
        long Id, ChangeRequestKind Kind, ChangeRequestStatus Status, long? TargetToiletId, string? ToiletName,
        DateTime CreatedAt, DateTime? ReviewedAt, string? ReviewNote);
}
