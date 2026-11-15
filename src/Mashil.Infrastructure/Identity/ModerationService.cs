using Mashil.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Mashil.Infrastructure.Identity;

public enum ReportOutcome { Created, Duplicate, NotFound, OwnComment }

public enum BlockOutcome { Blocked, Self, NotFound }

public sealed record ReportQueueItem(
    long CommentId, long ToiletId, Guid AuthorId, string Text, bool Hidden,
    int OpenReports, int TotalReports, IReadOnlyDictionary<ReportReason, int> Reasons, DateTime LastReportedAt,
    string? ToiletName = null, string? AuthorName = null, IReadOnlyList<string>? Details = null);

/// <summary>댓글 신고·사용자 차단·관리자 숨김 (UGC-03~05, SD-07, Apple 1.2).</summary>
public sealed class ModerationService(MashilDbContext db, IOptions<ModerationOptions> options, TimeProvider time)
{
    public async Task<(ReportOutcome Outcome, bool AutoHidden)> ReportAsync(
        long commentId, Guid reporterId, ReportReason reason, string? detail, CancellationToken ct)
    {
        var comment = await db.Comments.Where(c => c.Id == commentId).Select(c => new { c.UserId }).FirstOrDefaultAsync(ct);
        if (comment is null) return (ReportOutcome.NotFound, false);
        if (comment.UserId == reporterId) return (ReportOutcome.OwnComment, false);

        var now = time.GetUtcNow().UtcDateTime;
        db.CommentReports.Add(new CommentReport
        {
            CommentId = commentId,
            ReporterId = reporterId,
            Reason = reason,
            Detail = string.IsNullOrWhiteSpace(detail) ? null : detail.Trim() is { Length: > 500 } d ? d[..500] : detail.Trim(),
            CreatedAt = now,
        });
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return (ReportOutcome.Duplicate, false);
        }

        await db.Comments.Where(c => c.Id == commentId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.ReportCount, c => c.ReportCount + 1), ct);

        var threshold = options.Value.AutoHideReportCount;
        var autoHidden = threshold > 0 && await db.Comments
            .Where(c => c.Id == commentId && !c.Hidden && c.ReportCount >= threshold)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Hidden, true), ct) == 1;
        return (ReportOutcome.Created, autoHidden);
    }

    public async Task<BlockOutcome> BlockAsync(Guid blockerId, Guid blockedId, CancellationToken ct)
    {
        if (blockerId == blockedId) return BlockOutcome.Self;
        if (!await db.UserProfiles.AnyAsync(u => u.Id == blockedId, ct)) return BlockOutcome.NotFound;
        var now = time.GetUtcNow().UtcDateTime;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_blocks ("BlockerId", "BlockedId", "CreatedAt") VALUES ({blockerId}, {blockedId}, {now})
            ON CONFLICT DO NOTHING
            """, ct);
        return BlockOutcome.Blocked;
    }

    public Task UnblockAsync(Guid blockerId, Guid blockedId, CancellationToken ct) =>
        db.UserBlocks.Where(b => b.BlockerId == blockerId && b.BlockedId == blockedId).ExecuteDeleteAsync(ct);

    /// <summary>관리자 숨김·복원. 열린 신고는 숨기면 Actioned, 복원하면 Dismissed 로 닫는다.</summary>
    public async Task<bool> SetHiddenAsync(long commentId, bool hidden, Guid adminId, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var n = await db.Comments.Where(c => c.Id == commentId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Hidden, hidden), ct);
        if (n == 0) return false;
        var status = hidden ? ReportStatus.Actioned : ReportStatus.Dismissed;
        await db.CommentReports.Where(r => r.CommentId == commentId && r.Status == ReportStatus.Open)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, status)
                .SetProperty(r => r.ReviewedById, adminId)
                .SetProperty(r => r.ReviewedAt, now), ct);
        await tx.CommitAsync(ct);
        return true;
    }

    /// <summary>신고 큐: 댓글별로 묶어 열린 신고 수 순 (ADM-02).</summary>
    public async Task<IReadOnlyList<ReportQueueItem>> QueueAsync(ReportStatus? status, int limit, CancellationToken ct)
    {
        var reports = db.CommentReports.AsQueryable();
        if (status is not null) reports = reports.Where(r => r.Status == status);
        var commentIds = await reports
            .GroupBy(r => r.CommentId)
            .Select(g => new { CommentId = g.Key, Count = g.Count(), Last = g.Max(r => r.CreatedAt) })
            .OrderByDescending(g => g.Count).ThenByDescending(g => g.Last)
            .Take(limit)
            .Select(g => g.CommentId)
            .ToListAsync(ct);

        var comments = await db.Comments.Where(c => commentIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var all = await db.CommentReports.Where(r => commentIds.Contains(r.CommentId))
            .Select(r => new { r.CommentId, r.Reason, r.Status, r.CreatedAt, r.Detail }).ToListAsync(ct);
        // 관리자 화면에서 바로 판단할 수 있게 화장실 이름, 작성자 이름, 신고자가 적은 내용
        var toiletIds = comments.Values.Select(c => c.ToiletId).Distinct().ToList();
        var toiletNames = await db.Toilets.Where(t => toiletIds.Contains(t.Id)).ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        var authorIds = comments.Values.Select(c => c.UserId).Distinct().ToList();
        var authors = await db.UserProfiles.Where(u => authorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.Status == UserStatus.Deleted ? UserProfile.DeletedDisplayName : u.DisplayName, ct);

        return commentIds.Where(comments.ContainsKey).Select(id =>
        {
            var c = comments[id];
            var mine = all.Where(r => r.CommentId == id).ToList();
            return new ReportQueueItem(id, c.ToiletId, c.UserId, c.Text, c.Hidden,
                mine.Count(r => r.Status == ReportStatus.Open), mine.Count,
                mine.GroupBy(r => r.Reason).ToDictionary(g => g.Key, g => g.Count()),
                mine.Max(r => r.CreatedAt),
                toiletNames.GetValueOrDefault(c.ToiletId), authors.GetValueOrDefault(c.UserId),
                mine.Where(r => r.Detail != null).OrderByDescending(r => r.CreatedAt).Select(r => r.Detail!).Take(5).ToList());
        }).ToList();
    }
}
