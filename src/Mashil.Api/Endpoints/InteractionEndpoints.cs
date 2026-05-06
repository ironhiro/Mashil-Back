using System.Security.Claims;
using Mashil.Api.Auth;
using Mashil.Domain;
using Mashil.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Mashil.Api.Endpoints;

/// <summary>
/// 평점 / 댓글 / 변경 요청. 사용자 ID 는 JWT sub claim 에서 추출.
/// 관리자 엔드포인트는 RequireRole("Admin") 으로 게이트.
/// </summary>
public static class InteractionEndpoints
{
    public static void MapInteractionEndpoints(this IEndpointRouteBuilder app)
    {
        var ratings = app.MapGroup("/api/toilets/{toiletId:long}/ratings").WithTags("Ratings");

        ratings.MapPost("", async (
            long toiletId,
            RatingInput input,
            ClaimsPrincipal principal,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.GetUserIdOrThrow();
            if (input.Score is < 1 or > 5)
                return Results.BadRequest(new { error = "score must be 1..5" });

            var existing = await db.Ratings.FirstOrDefaultAsync(r => r.UserId == userId && r.ToiletId == toiletId, ct);
            if (existing is null)
            {
                db.Ratings.Add(new Rating { UserId = userId, ToiletId = toiletId, Score = input.Score });
            }
            else
            {
                existing.Score = input.Score;
                existing.UpdatedAt = DateTime.UtcNow;
            }
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        })
        .RequireAuthorization()
        .WithSummary("화장실 별점 등록/갱신 (인증 필요)")
        .WithDescription("(user, toilet) 유니크 — 같은 사용자가 같은 화장실에 다시 POST 하면 기존 점수 갱신. `score` 1~5 범위 외면 400.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        var comments = app.MapGroup("/api/toilets/{toiletId:long}/comments").WithTags("Comments");

        comments.MapGet("", async (long toiletId, MashilDbContext db, CancellationToken ct) =>
        {
            var list = await db.Comments
                .Where(c => c.ToiletId == toiletId && !c.Hidden)
                .OrderByDescending(c => c.CreatedAt)
                .Take(100)
                .ToListAsync(ct);
            return Results.Ok(list);
        })
        .WithSummary("화장실 댓글 목록 (공개)")
        .WithDescription("`Hidden = true` (관리자 숨김) 행은 제외, 최신순 100건. 인증 불필요.")
        .Produces<IReadOnlyList<Comment>>();

        comments.MapPost("", async (
            long toiletId,
            CommentInput input,
            ClaimsPrincipal principal,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            var userId = principal.GetUserIdOrThrow();
            if (string.IsNullOrWhiteSpace(input.Text))
                return Results.BadRequest(new { error = "text required" });

            var c = new Comment { UserId = userId, ToiletId = toiletId, Text = input.Text.Trim() };
            db.Comments.Add(c);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/toilets/{toiletId}/comments/{c.Id}", c);
        })
        .RequireAuthorization()
        .WithSummary("댓글 작성 (인증 필요)")
        .WithDescription("빈 문자열/공백만이면 400. 작성자 ID 는 JWT 에서 추출 — 페이로드로 받지 않음.")
        .Produces<Comment>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status400BadRequest)
        .Produces(StatusCodes.Status401Unauthorized);

        var changes = app.MapGroup("/api/change-requests").WithTags("ChangeRequests");

        changes.MapPost("", async (
            ChangeRequestInput input,
            ClaimsPrincipal principal,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            var requesterId = principal.GetUserIdOrThrow();
            var cr = new ChangeRequest
            {
                RequesterId = requesterId,
                TargetToiletId = input.TargetToiletId,
                Kind = input.Kind,
                PayloadJson = input.PayloadJson ?? "{}",
            };
            db.ChangeRequests.Add(cr);
            await db.SaveChangesAsync(ct);
            return Results.Created($"/api/change-requests/{cr.Id}", cr);
        })
        .RequireAuthorization()
        .WithSummary("변경 요청 제출 (인증 필요)")
        .WithDescription("화장실 추가/수정/삭제 요청을 큐에 적재. 즉시 반영 안 됨 — 관리자 승인 후 별도 서비스가 적용. 상태는 항상 `Pending` 으로 시작.")
        .Produces<ChangeRequest>(StatusCodes.Status201Created)
        .Produces(StatusCodes.Status401Unauthorized);

        // Admin endpoints
        var admin = app.MapGroup("/api/admin/change-requests").WithTags("Admin").RequireAuthorization("Admin");

        admin.MapGet("", async (ChangeRequestStatus? status, MashilDbContext db, CancellationToken ct) =>
        {
            var query = db.ChangeRequests.AsQueryable();
            if (status is not null) query = query.Where(c => c.Status == status);
            var list = await query.OrderByDescending(c => c.CreatedAt).Take(200).ToListAsync(ct);
            return Results.Ok(list);
        })
        .WithSummary("변경 요청 큐 조회 (관리자)")
        .WithDescription("`status` 필터 (`Pending` / `Approved` / `Rejected`) — 생략 시 전체. 최신순 200건. `Admin` 역할 필요.")
        .Produces<IReadOnlyList<ChangeRequest>>()
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden);

        admin.MapPatch("/{id:long}", async (
            long id,
            ChangeRequestReview review,
            ClaimsPrincipal principal,
            MashilDbContext db,
            CancellationToken ct) =>
        {
            var cr = await db.ChangeRequests.FindAsync([id], ct);
            if (cr is null) return Results.NotFound();
            cr.Status = review.Approve ? ChangeRequestStatus.Approved : ChangeRequestStatus.Rejected;
            cr.ReviewedAt = DateTime.UtcNow;
            cr.ReviewedById = principal.GetUserIdOrThrow();
            cr.ReviewNote = review.Note;
            await db.SaveChangesAsync(ct);
            // TODO: when Approved, apply the payload to the target Toilet in a follow-up service.
            return Results.NoContent();
        })
        .WithSummary("변경 요청 승인/반려 (관리자)")
        .WithDescription("`approve = true` → `Approved`, `false` → `Rejected`. 승인된 요청의 실제 Toilet 반영은 후속 서비스 (현재는 상태만 변경). 같은 요청에 다시 PATCH 하면 마지막 결정으로 덮어씀.")
        .Produces(StatusCodes.Status204NoContent)
        .Produces(StatusCodes.Status401Unauthorized)
        .Produces(StatusCodes.Status403Forbidden)
        .Produces(StatusCodes.Status404NotFound);
    }

    /// <summary>별점 페이로드.</summary>
    /// <param name="Score">1~5 정수.</param>
    public sealed record RatingInput(int Score);

    /// <summary>댓글 페이로드.</summary>
    /// <param name="Text">본문 (앞뒤 공백 trim 후 저장; 빈 값 거부).</param>
    public sealed record CommentInput(string Text);

    /// <summary>변경 요청 페이로드.</summary>
    /// <param name="Kind">요청 종류 (`Add` / `Update` / `Delete`).</param>
    /// <param name="TargetToiletId">대상 toilet id — `Add` 의 경우 null.</param>
    /// <param name="PayloadJson">JSON 문자열 — `Update` 시 변경할 필드들, `Add` 시 새 toilet 데이터. null 이면 빈 객체로 저장.</param>
    public sealed record ChangeRequestInput(ChangeRequestKind Kind, long? TargetToiletId, string? PayloadJson);

    /// <summary>관리자 검토 페이로드.</summary>
    /// <param name="Approve">true 승인 / false 반려.</param>
    /// <param name="Note">검토 메모 (선택). 사용자에게 노출 가능성 — 친절하게 작성 권장.</param>
    public sealed record ChangeRequestReview(bool Approve, string? Note);
}
