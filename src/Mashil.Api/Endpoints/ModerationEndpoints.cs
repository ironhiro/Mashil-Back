using System.Security.Claims;
using Mashil.Api.Auth;
using Mashil.Domain;
using Mashil.Infrastructure;
using Mashil.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Api.Endpoints;

/// <summary>댓글 신고, 사용자 차단, 관리자 신고 큐 (UGC-03~05, ADM-02, SD-07).</summary>
public static class ModerationEndpoints
{
    public static void MapModerationEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/comments/{commentId:long}/reports", async (
                long commentId, ReportInput input, ClaimsPrincipal principal, ModerationService moderation, CancellationToken ct) =>
            {
                // System.Text.Json 은 정의되지 않은 숫자(예: 9)도 enum 으로 받으므로 직접 거른다 (#17)
                if (!input.HasValidReason) return AuthEndpoints.Error("invalid_reason", 400);
                var (outcome, _) = await moderation.ReportAsync(commentId, principal.GetUserIdOrThrow(), input.Reason, input.Detail, ct);
                return outcome switch
                {
                    ReportOutcome.Created => Results.StatusCode(StatusCodes.Status201Created),
                    ReportOutcome.Duplicate => AuthEndpoints.Error("already_reported", 409),
                    ReportOutcome.OwnComment => AuthEndpoints.Error("own_comment", 400),
                    _ => Results.NotFound(),
                };
            })
            .RequireAuthorization()
            .RequireConsent()
            .WithTags("Moderation")
            .WithSummary("댓글 신고")
            .WithDescription("reason = `Spam` / `Abuse` / `False` / `Other` (그 밖의 값은 400 `invalid_reason`), detail 은 선택 (500자). 같은 댓글은 한 번만 (409). 서로 다른 사용자 신고가 `Moderation:AutoHideReportCount`(기본 3)에 이르면 관리자 확인 전까지 자동 숨김.")
            .Produces(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status409Conflict)
            .Produces(StatusCodes.Status404NotFound);

        var blocks = app.MapGroup("/api/users").WithTags("Moderation").RequireAuthorization();

        blocks.MapPost("/{userId:guid}/block", async (Guid userId, ClaimsPrincipal principal, ModerationService moderation, CancellationToken ct) =>
                await moderation.BlockAsync(principal.GetUserIdOrThrow(), userId, ct) switch
                {
                    BlockOutcome.Blocked => Results.NoContent(),
                    BlockOutcome.Self => AuthEndpoints.Error("cannot_block_self", 400),
                    _ => Results.NotFound(),
                })
            .WithSummary("사용자 차단")
            .WithDescription("차단한 사용자의 댓글은 내 화면의 댓글 목록에서 빠진다. 이미 차단했으면 그대로 204.")
            .Produces(StatusCodes.Status204NoContent);

        blocks.MapDelete("/{userId:guid}/block", async (Guid userId, ClaimsPrincipal principal, ModerationService moderation, CancellationToken ct) =>
            {
                await moderation.UnblockAsync(principal.GetUserIdOrThrow(), userId, ct);
                return Results.NoContent();
            })
            .WithSummary("차단 해제")
            .Produces(StatusCodes.Status204NoContent);

        blocks.MapGet("/me/blocks", async (ClaimsPrincipal principal, MashilDbContext db, CancellationToken ct) =>
            {
                var me = principal.GetUserIdOrThrow();
                var list = await db.UserBlocks.Where(b => b.BlockerId == me)
                    .Join(db.UserProfiles, b => b.BlockedId, u => u.Id, (b, u) => new { b.BlockedId, u.DisplayName, u.Status, b.CreatedAt })
                    .OrderByDescending(x => x.CreatedAt)
                    .ToListAsync(ct);
                return Results.Ok(list.Select(x => new BlockedUser(x.BlockedId, CommentAuthor.Name(x.DisplayName, x.Status), x.CreatedAt)));
            })
            .WithSummary("내 차단 목록")
            .Produces<IReadOnlyList<BlockedUser>>();

        var admin = app.MapGroup("/api/admin").WithTags("Admin").RequireAuthorization("Admin");

        admin.MapGet("/comment-reports", async (ReportStatus? status, int? limit, ModerationService moderation, CancellationToken ct) =>
                Results.Ok(await moderation.QueueAsync(status ?? ReportStatus.Open, Math.Clamp(limit ?? 100, 1, 500), ct)))
            .WithSummary("댓글 신고 큐 (관리자)")
            .WithDescription("댓글별로 묶어 신고 수 순. status 기본 `Open`.")
            .Produces<IReadOnlyList<ReportQueueItem>>();

        admin.MapPatch("/comments/{commentId:long}", async (
                long commentId, HideInput input, ClaimsPrincipal principal, ModerationService moderation, CancellationToken ct) =>
                await moderation.SetHiddenAsync(commentId, input.Hidden, principal.GetUserIdOrThrow(), ct)
                    ? Results.NoContent()
                    : Results.NotFound())
            .WithSummary("댓글 숨김·복원 (관리자)")
            .WithDescription("hidden=true → 열린 신고 Actioned, false(복원) → Dismissed.")
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status404NotFound);
    }

    public sealed record ReportInput(ReportReason Reason, string? Detail)
    {
        /// <summary>Spam / Abuse / False / Other 중 하나.</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public bool HasValidReason => Enum.IsDefined(Reason);
    }
    public sealed record HideInput(bool Hidden);
    public sealed record BlockedUser(Guid UserId, string DisplayName, DateTime BlockedAt);
}

public static class CommentAuthor
{
    public const string Anonymous = "익명";

    public static string Name(string? displayName, UserStatus status) =>
        status == UserStatus.Deleted ? UserProfile.DeletedDisplayName : displayName ?? Anonymous;
}
