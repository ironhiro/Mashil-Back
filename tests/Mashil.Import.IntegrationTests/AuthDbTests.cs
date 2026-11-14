using System.Security.Cryptography;
using Mashil.Domain;
using Mashil.Infrastructure;
using Mashil.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Mashil.Import.IntegrationTests;

/// <summary>네트워크 없이 정해 둔 신원을 돌려주는 IdP. 연결 해제 호출을 기록한다.</summary>
internal sealed class FakeProvider(AuthProvider provider) : IIdentityProvider
{
    public AuthProvider Provider => provider;
    public bool Enabled => true;
    public string? NextNonceClaim { get; set; }
    public RevokeOutcome RevokeWith { get; set; } = RevokeOutcome.Done;
    public List<(RevocationTarget Target, VerifiedIdentity? Fresh)> Revoked { get; } = [];

    /// <summary>IdToken 칸에 "sub|nickname" 을 넣으면 그 신원으로 검증된 것으로 친다.</summary>
    public Task<VerifiedIdentity> VerifyAsync(SocialCredential c, string? expectedNonce, CancellationToken ct)
    {
        if (c.IdToken is null || c.IdToken == "bad") throw AuthFailure.Invalid("fake");
        if (expectedNonce is not null && NextNonceClaim is not null && NextNonceClaim != expectedNonce) throw AuthFailure.Invalid("nonce");
        var parts = c.IdToken.Split('|');
        return Task.FromResult(new VerifiedIdentity(provider, parts[0], "client", null, parts.ElementAtOrDefault(1))
        {
            AppleRefreshToken = provider == AuthProvider.Apple ? "apple-refresh-" + parts[0] : null,
        });
    }

    public Task<RevokeResult> RevokeAsync(RevocationTarget target, VerifiedIdentity? fresh, CancellationToken ct)
    {
        Revoked.Add((target, fresh));
        return Task.FromResult(new RevokeResult(RevokeWith, RevokeWith == RevokeOutcome.Done ? null : "fake error"));
    }
}

internal sealed class AuthHarness
{
    public readonly FakeProvider Kakao = new(AuthProvider.Kakao);
    public readonly FakeProvider Apple = new(AuthProvider.Apple);
    public readonly SecretBox Box = new(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
    private readonly IdentityProviderRegistry _registry;
    private readonly TestDatabase _tdb;

    public AuthHarness(TestDatabase tdb)
    {
        _tdb = tdb;
        _registry = new IdentityProviderRegistry([Kakao, Apple]);
    }

    public MashilDbContext Db() => _tdb.NewContext();

    private static readonly IOptions<OAuthOptions> OAuth = Options.Create(new OAuthOptions());
    private static readonly IOptions<ConsentOptions> Consent = Options.Create(new ConsentOptions());

    public RefreshTokenService Refresh(MashilDbContext db) => new(db,
        new AccessTokenIssuer(Options.Create(new JwtOptions { SigningKey = new string('k', 32) }), TimeProvider.System),
        OAuth, TimeProvider.System, NullLogger<RefreshTokenService>.Instance);

    public NonceService Nonces(MashilDbContext db) => new(db, OAuth, TimeProvider.System);
    public ConsentService Consents(MashilDbContext db) => new(db, Consent, TimeProvider.System);

    public OAuthLoginService Login(MashilDbContext db) => new(db, _registry, Nonces(db), Refresh(db), Consents(db), Box,
        TimeProvider.System, NullLogger<OAuthLoginService>.Instance);

    public IdpRevocationProcessor Revocations(MashilDbContext db) =>
        new(db, _registry, Box, TimeProvider.System, NullLogger<IdpRevocationProcessor>.Instance);

    public AccountDeletionService Deletion(MashilDbContext db) => new(db, _registry, Nonces(db), Revocations(db), Box,
        TimeProvider.System, NullLogger<AccountDeletionService>.Instance);

    public ModerationService Moderation(MashilDbContext db, int autoHide = 3) =>
        new(db, Options.Create(new ModerationOptions { AutoHideReportCount = autoHide }), TimeProvider.System);

    public async Task<LoginResult> SignIn(AuthProvider p, string sub, string? nickname = null, string? nonce = null)
    {
        await using var db = Db();
        return await Login(db).SignInAsync(p, new SocialCredential(IdToken: nickname is null ? sub : $"{sub}|{nickname}"), nonce, "ios", null, default);
    }
}

public class AuthDbTests
{
    [DbFact]
    public async Task First_login_creates_user_and_identity_second_login_reuses_it()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var h = new AuthHarness(tdb);

        var first = await h.SignIn(AuthProvider.Kakao, "4242", "마실러");
        Assert.True(first.IsNewUser);
        Assert.True(first.ConsentRequired);
        Assert.Equal("마실러", first.User.DisplayName);

        var again = await h.SignIn(AuthProvider.Kakao, "4242", "바뀐닉");
        Assert.False(again.IsNewUser);
        Assert.Equal(first.User.Id, again.User.Id);
        Assert.Equal("마실러", again.User.DisplayName); // 사용자가 정한 이름을 IdP 닉네임으로 덮지 않음

        var other = await h.SignIn(AuthProvider.Apple, "4242"); // 같은 sub 라도 제공자가 다르면 다른 사람
        Assert.NotEqual(first.User.Id, other.User.Id);

        await using var db = h.Db();
        Assert.Equal(2, await db.UserIdentities.CountAsync());
        var apple = await db.UserIdentities.SingleAsync(i => i.Provider == AuthProvider.Apple);
        Assert.Equal("apple-refresh-4242", h.Box.Open(apple.AppleRefreshTokenEnc)); // 암호화 저장
    }

    [DbFact]
    public async Task Nonce_is_single_use_and_expires()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var h = new AuthHarness(tdb);
        string nonce;
        await using (var db = h.Db()) (nonce, _) = await h.Nonces(db).IssueAsync(default);

        await h.SignIn(AuthProvider.Kakao, "1", nonce: nonce);
        var replay = await Assert.ThrowsAsync<AuthFailure>(() => h.SignIn(AuthProvider.Kakao, "1", nonce: nonce));
        Assert.Equal("nonce_invalid", replay.Code);
        var unknown = await Assert.ThrowsAsync<AuthFailure>(() => h.SignIn(AuthProvider.Kakao, "1", nonce: "never-issued"));
        Assert.Equal("nonce_invalid", unknown.Code);

        await using (var db = h.Db())
        {
            Assert.DoesNotContain(nonce, await db.AuthNonces.Select(n => n.NonceHash).ToListAsync()); // 원문 저장 안 함
            var (expired, _) = await h.Nonces(db).IssueAsync(default);
            var hash = TokenHash.Sha256Hex(expired);
            await db.AuthNonces.Where(n => n.NonceHash == hash)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.ExpiresAt, DateTime.UtcNow.AddMinutes(-1)));
            await Assert.ThrowsAsync<AuthFailure>(() => h.Nonces(db).ConsumeAsync(expired, default));
        }
    }

    [DbFact]
    public async Task Refresh_rotates_and_reuse_revokes_whole_family()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var h = new AuthHarness(tdb);
        var login = await h.SignIn(AuthProvider.Kakao, "1");
        var otherDevice = await h.SignIn(AuthProvider.Kakao, "1");

        (TokenPair Tokens, UserProfile User) r1;
        await using (var db = h.Db()) r1 = await h.Refresh(db).RotateAsync(login.Tokens.RefreshToken, default);
        Assert.NotEqual(login.Tokens.RefreshToken, r1.Tokens.RefreshToken);
        Assert.Equal(login.User.Id, r1.User.Id);

        // 이미 쓴 토큰 재사용 → 탈취 의심 → 그 family(r1 포함) 전부 폐기
        await using (var db = h.Db())
            Assert.Equal("reauth_required", (await Assert.ThrowsAsync<AuthFailure>(() => h.Refresh(db).RotateAsync(login.Tokens.RefreshToken, default))).Code);
        await using (var db = h.Db())
            await Assert.ThrowsAsync<AuthFailure>(() => h.Refresh(db).RotateAsync(r1.Tokens.RefreshToken, default));

        // 다른 기기 세션은 영향 없음
        await using (var db = h.Db())
            await h.Refresh(db).RotateAsync(otherDevice.Tokens.RefreshToken, default);

        await using (var db = h.Db())
            Assert.DoesNotContain(login.Tokens.RefreshToken, await db.RefreshTokens.Select(t => t.TokenHash).ToListAsync());
    }

    [DbFact]
    public async Task Concurrent_refresh_with_same_token_lets_only_one_win()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var h = new AuthHarness(tdb);
        var login = await h.SignIn(AuthProvider.Kakao, "1");

        async Task<bool> Try()
        {
            await using var db = h.Db();
            try { await h.Refresh(db).RotateAsync(login.Tokens.RefreshToken, default); return true; }
            catch (AuthFailure) { return false; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => Try()));
        Assert.True(results.Count(ok => ok) <= 1);
    }

    [DbFact]
    public async Task Logout_revokes_device_family()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var h = new AuthHarness(tdb);
        var login = await h.SignIn(AuthProvider.Kakao, "1");
        await using (var db = h.Db()) await h.Refresh(db).RevokeFamilyAsync(login.Tokens.RefreshToken, default);
        await using (var db = h.Db())
            await Assert.ThrowsAsync<AuthFailure>(() => h.Refresh(db).RotateAsync(login.Tokens.RefreshToken, default));
    }

    [DbFact]
    public async Task Consent_status_and_version_check()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var h = new AuthHarness(tdb);
        var login = await h.SignIn(AuthProvider.Kakao, "1");

        await using var db = h.Db();
        var consents = h.Consents(db);
        Assert.False(await consents.AgreeAsync(login.User.Id, [(ConsentDocType.Terms, "0.9")], default));
        Assert.True(await consents.AgreeAsync(login.User.Id, [(ConsentDocType.Terms, "1"), (ConsentDocType.Privacy, "1")], default));
        Assert.True(await consents.IsRequiredAsync(login.User.Id, default)); // 위치기반 약관 남음
        Assert.True(await consents.AgreeAsync(login.User.Id, [(ConsentDocType.LocationTerms, "1"), (ConsentDocType.Terms, "1")], default)); // 중복 무시
        Assert.False(await consents.IsRequiredAsync(login.User.Id, default));
        Assert.False((await h.SignIn(AuthProvider.Kakao, "1")).ConsentRequired);
    }

    [DbFact]
    public async Task Delete_account_wipes_personal_data_unlinks_idp_and_relogin_is_new_user()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var h = new AuthHarness(tdb);
        var me = await h.SignIn(AuthProvider.Kakao, "4242", "지울사람");
        var other = await h.SignIn(AuthProvider.Kakao, "9999", "남");
        var toiletId = await SeedToilet(h);

        await using (var db = h.Db())
        {
            db.Comments.Add(new Comment { UserId = me.User.Id, ToiletId = toiletId, Text = "깨끗해요" });
            db.Ratings.Add(new Rating { UserId = me.User.Id, ToiletId = toiletId, Score = 5 });
            await db.SaveChangesAsync();
            await h.Moderation(db).BlockAsync(me.User.Id, other.User.Id, default);
            await h.Moderation(db).BlockAsync(other.User.Id, me.User.Id, default);
            await h.Consents(db).AgreeAsync(me.User.Id, [(ConsentDocType.Terms, "1")], default);
        }

        // 다른 계정으로 재인증하면 거부
        await using (var db = h.Db())
        {
            var f = await Assert.ThrowsAsync<AuthFailure>(() => h.Deletion(db).DeleteAsync(me.User.Id, AuthProvider.Kakao,
                new SocialCredential(IdToken: "9999"), null, null, default));
            Assert.Equal("identity_mismatch", f.Code);
        }
        await using (var db = h.Db())
            await h.Deletion(db).DeleteAsync(me.User.Id, AuthProvider.Kakao, new SocialCredential(IdToken: "4242"), null, null, default);

        await using (var db = h.Db())
        {
            var u = await db.UserProfiles.SingleAsync(x => x.Id == me.User.Id);
            Assert.Equal((UserStatus.Deleted, null, null), (u.Status, u.DisplayName, u.Email));
            Assert.NotNull(u.DeletedAt);
            Assert.False(await db.UserIdentities.AnyAsync(i => i.UserId == me.User.Id));
            Assert.False(await db.RefreshTokens.AnyAsync(t => t.UserId == me.User.Id));
            Assert.False(await db.UserBlocks.AnyAsync(b => b.BlockerId == me.User.Id || b.BlockedId == me.User.Id));
            Assert.Equal(1, await db.Comments.CountAsync(c => c.UserId == me.User.Id)); // 댓글은 "탈퇴한 사용자"로 남음
            Assert.Equal(1, await db.UserConsents.CountAsync(c => c.UserId == me.User.Id)); // 동의 증빙 보관
            Assert.False(await db.IdpRevocations.AnyAsync()); // 바로 끊었으므로 대기열 비어 있음
        }
        var (target, fresh) = Assert.Single(h.Kakao.Revoked);
        Assert.Equal("4242", target.Subject);
        Assert.Equal("4242", fresh!.Subject);

        // 탈퇴 후 refresh 불가, 같은 카카오 계정으로 다시 로그인하면 새 사용자 (USR-05)
        await using (var db = h.Db())
            await Assert.ThrowsAsync<AuthFailure>(() => h.Refresh(db).RotateAsync(me.Tokens.RefreshToken, default));
        var again = await h.SignIn(AuthProvider.Kakao, "4242", "돌아옴");
        Assert.True(again.IsNewUser);
        Assert.NotEqual(me.User.Id, again.User.Id);
    }

    [DbFact]
    public async Task Failed_revocation_stays_queued_with_backoff_then_gives_up_and_forgets_subject()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var h = new AuthHarness(tdb);
        var me = await h.SignIn(AuthProvider.Apple, "001.x");
        h.Apple.RevokeWith = RevokeOutcome.Retry;

        await using (var db = h.Db())
            await h.Deletion(db).DeleteAsync(me.User.Id, AuthProvider.Apple, new SocialCredential(IdToken: "001.x"), null, null, default);

        await using (var db = h.Db())
        {
            var row = await db.IdpRevocations.SingleAsync();
            Assert.Equal((RevocationStatus.Pending, 1), (row.Status, row.Attempts));
            Assert.True(row.NextAttemptAt > DateTime.UtcNow);
            Assert.Equal("apple-refresh-001.x", h.Box.Open(row.AppleRefreshTokenEnc));
            Assert.Equal(0, await h.Revocations(db).ProcessDueAsync(10, default)); // 아직 때가 아님

            await db.IdpRevocations.ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Attempts, IdpRevocationProcessor.MaxAttempts - 1)
                .SetProperty(r => r.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
        }
        await using (var db = h.Db()) Assert.Equal(1, await h.Revocations(db).ProcessDueAsync(10, default));
        await using (var db = h.Db())
        {
            var row = await db.IdpRevocations.SingleAsync();
            Assert.Equal(RevocationStatus.Failed, row.Status);
            Assert.Null(row.ProviderSubject);
            Assert.Null(row.AppleRefreshTokenEnc);
        }

        // Worker 재시도가 성공하면 행이 사라진다
        var other = await h.SignIn(AuthProvider.Apple, "002.y");
        await using (var db = h.Db())
            await h.Deletion(db).DeleteAsync(other.User.Id, AuthProvider.Apple, new SocialCredential(IdToken: "002.y"), null, null, default);
        h.Apple.RevokeWith = RevokeOutcome.Done;
        await using (var db = h.Db())
            await db.IdpRevocations.Where(r => r.Status == RevocationStatus.Pending)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAttemptAt, DateTime.UtcNow.AddMinutes(-1)));
        await using (var db = h.Db()) await h.Revocations(db).ProcessDueAsync(10, default);
        await using (var db = h.Db()) Assert.False(await db.IdpRevocations.AnyAsync(r => r.Status == RevocationStatus.Pending));
    }

    [DbFact]
    public async Task Reports_are_unique_auto_hide_at_threshold_and_admin_review_closes_them()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var h = new AuthHarness(tdb);
        var author = await h.SignIn(AuthProvider.Kakao, "a");
        var reporters = new List<Guid>();
        for (var i = 0; i < 3; i++) reporters.Add((await h.SignIn(AuthProvider.Kakao, "r" + i)).User.Id);
        var toiletId = await SeedToilet(h);
        long commentId;
        await using (var db = h.Db())
        {
            var c = new Comment { UserId = author.User.Id, ToiletId = toiletId, Text = "광고" };
            db.Comments.Add(c);
            await db.SaveChangesAsync();
            commentId = c.Id;
        }

        await using (var db = h.Db())
        {
            var m = h.Moderation(db);
            Assert.Equal(ReportOutcome.OwnComment, (await m.ReportAsync(commentId, author.User.Id, ReportReason.Spam, null, default)).Outcome);
            Assert.Equal(ReportOutcome.NotFound, (await m.ReportAsync(-1, reporters[0], ReportReason.Spam, null, default)).Outcome);
            Assert.Equal((ReportOutcome.Created, false), await m.ReportAsync(commentId, reporters[0], ReportReason.Spam, "광고", default));
        }
        await using (var db = h.Db())
            Assert.Equal(ReportOutcome.Duplicate, (await h.Moderation(db).ReportAsync(commentId, reporters[0], ReportReason.Abuse, null, default)).Outcome);
        await using (var db = h.Db()) await h.Moderation(db).ReportAsync(commentId, reporters[1], ReportReason.Abuse, null, default);
        await using (var db = h.Db())
            Assert.Equal((ReportOutcome.Created, true), await h.Moderation(db).ReportAsync(commentId, reporters[2], ReportReason.Spam, null, default));

        await using (var db = h.Db())
        {
            var c = await db.Comments.SingleAsync(x => x.Id == commentId);
            Assert.Equal((true, 3), (c.Hidden, c.ReportCount));
            var queue = await h.Moderation(db).QueueAsync(ReportStatus.Open, 10, default);
            var item = Assert.Single(queue);
            Assert.Equal((3, 2, 1), (item.OpenReports, item.Reasons[ReportReason.Spam], item.Reasons[ReportReason.Abuse]));

            // 관리자가 검토 후 복원 → 신고는 Dismissed
            Assert.True(await h.Moderation(db).SetHiddenAsync(commentId, false, reporters[0], default));
        }
        await using (var db = h.Db())
        {
            Assert.False((await db.Comments.SingleAsync(x => x.Id == commentId)).Hidden);
            Assert.All(await db.CommentReports.ToListAsync(), r => Assert.Equal(ReportStatus.Dismissed, r.Status));
            Assert.Empty(await h.Moderation(db).QueueAsync(ReportStatus.Open, 10, default));
            Assert.False(await h.Moderation(db).SetHiddenAsync(-1, true, reporters[0], default));
        }
    }

    [DbFact]
    public async Task Block_is_idempotent_and_cannot_target_self()
    {
        await using var tdb = await TestDatabase.CreateAsync();
        var h = new AuthHarness(tdb);
        var a = (await h.SignIn(AuthProvider.Kakao, "a")).User.Id;
        var b = (await h.SignIn(AuthProvider.Kakao, "b")).User.Id;

        await using var db = h.Db();
        var m = h.Moderation(db);
        Assert.Equal(BlockOutcome.Self, await m.BlockAsync(a, a, default));
        Assert.Equal(BlockOutcome.NotFound, await m.BlockAsync(a, Guid.NewGuid(), default));
        Assert.Equal(BlockOutcome.Blocked, await m.BlockAsync(a, b, default));
        Assert.Equal(BlockOutcome.Blocked, await m.BlockAsync(a, b, default));
        Assert.Equal(1, await db.UserBlocks.CountAsync());
        await m.UnblockAsync(a, b, default);
        Assert.Equal(0, await db.UserBlocks.CountAsync());
    }

    private static async Task<long> SeedToilet(AuthHarness h)
    {
        await using var db = h.Db();
        var t = new Toilet { ManagementNo = "T-" + Guid.NewGuid().ToString("N")[..8], Name = "테스트", Kind = "공중화장실" };
        db.Toilets.Add(t);
        await db.SaveChangesAsync();
        return t.Id;
    }
}
