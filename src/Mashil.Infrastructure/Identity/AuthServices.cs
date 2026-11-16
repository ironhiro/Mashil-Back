using Mashil.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Mashil.Infrastructure.Identity;

public sealed record TokenPair(string AccessToken, DateTime AccessTokenExpiresAt, string RefreshToken, DateTime RefreshTokenExpiresAt);

/// <summary>1회용 nonce. 발급할 때 만료된 것을 같이 지운다.</summary>
public sealed class NonceService(MashilDbContext db, IOptions<OAuthOptions> options, TimeProvider time)
{
    public async Task<(string Nonce, DateTime ExpiresAt)> IssueAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        await db.AuthNonces.Where(n => n.ExpiresAt < now.AddHours(-1)).ExecuteDeleteAsync(ct);
        var nonce = TokenHash.Random();
        var expires = now.AddMinutes(options.Value.NonceMinutes);
        db.AuthNonces.Add(new AuthNonce { NonceHash = TokenHash.Sha256Hex(nonce), ExpiresAt = expires });
        await db.SaveChangesAsync(ct);
        return (nonce, expires);
    }

    /// <summary>원자적으로 소비 — 두 요청이 같은 nonce 를 써도 하나만 성공한다.</summary>
    public async Task ConsumeAsync(string nonce, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var hash = TokenHash.Sha256Hex(nonce);
        var n = await db.AuthNonces
            .Where(x => x.NonceHash == hash && x.ConsumedAt == null && x.ExpiresAt > now)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.ConsumedAt, now), ct);
        if (n != 1) throw new AuthFailure("nonce_invalid", 401, "nonce 가 없거나 만료됐거나 이미 사용됨");
    }
}

/// <summary>
/// Refresh Token 회전 (SD-11). 쓴 토큰은 즉시 폐기하고 같은 family 로 새 토큰을 준다.
/// 이미 폐기된 토큰이 다시 오면 탈취로 보고 family 전체를 폐기한다.
/// 클라이언트는 갱신 요청을 한 번에 하나만 보내야 한다 (동시 갱신은 재사용으로 판정됨).
/// </summary>
public sealed class RefreshTokenService(
    MashilDbContext db,
    AccessTokenIssuer issuer,
    IOptions<OAuthOptions> options,
    TimeProvider time,
    ILogger<RefreshTokenService> logger)
{
    public async Task<TokenPair> IssueAsync(UserProfile user, string? device, CancellationToken ct, string? familyId = null)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var raw = TokenHash.Random();
        var row = new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenHash.Sha256Hex(raw),
            FamilyId = familyId ?? Guid.NewGuid().ToString("N"),
            DeviceLabel = NormalizeDevice(device),
            CreatedAt = now,
            ExpiresAt = now.AddDays(options.Value.RefreshTokenDays),
        };
        db.RefreshTokens.Add(row);
        await db.SaveChangesAsync(ct);
        var (access, accessExp) = issuer.Issue(user);
        return new TokenPair(access, accessExp, raw, row.ExpiresAt);
    }

    public async Task<(TokenPair Tokens, UserProfile User)> RotateAsync(string rawToken, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var hash = TokenHash.Sha256Hex(rawToken);
        var row = await db.RefreshTokens.AsNoTracking().FirstOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (row is null || row.ExpiresAt <= now) throw new AuthFailure("reauth_required", 401, "refresh token 없음 또는 만료");

        // 조건부 UPDATE 로 폐기 — 0행이면 이미 누군가 썼다는 뜻
        var claimed = row.RevokedAt is null && await db.RefreshTokens
            .Where(t => t.Id == row.Id && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct) == 1;
        if (!claimed)
        {
            await db.RefreshTokens.Where(t => t.FamilyId == row.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
            logger.LogWarning("폐기된 refresh token 재사용 — family {Family} 전체 폐기 (user {User})", row.FamilyId, row.UserId);
            throw new AuthFailure("reauth_required", 401, "refresh token 재사용");
        }

        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == row.UserId, ct);
        if (user is null || user.Status != UserStatus.Active) throw new AuthFailure("reauth_required", 401, "비활성 사용자");
        return (await IssueAsync(user, row.DeviceLabel, ct, row.FamilyId), user);
    }

    /// <summary>로그아웃: 이 기기의 family 를 폐기. 모르는 토큰이어도 성공으로 친다.</summary>
    public async Task RevokeFamilyAsync(string rawToken, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var hash = TokenHash.Sha256Hex(rawToken);
        var family = await db.RefreshTokens.Where(t => t.TokenHash == hash).Select(t => t.FamilyId).FirstOrDefaultAsync(ct);
        if (family is null) return;
        await db.RefreshTokens.Where(t => t.FamilyId == family && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    private static string? NormalizeDevice(string? device) =>
        device?.Trim().ToLowerInvariant() switch
        {
            "ios" => "ios",
            "android" => "android",
            "web" => "web",
            null or "" => null,
            _ => "other",
        };
}

public sealed record ConsentState(ConsentDocType DocType, string Version, bool Agreed);

public sealed class ConsentService(MashilDbContext db, IOptions<ConsentOptions> options, TimeProvider time)
{
    public bool EnforceOnWrite => options.Value.EnforceOnWrite;

    public async Task<IReadOnlyList<ConsentState>> StatusAsync(Guid userId, CancellationToken ct)
    {
        var agreed = await db.UserConsents.Where(c => c.UserId == userId)
            .Select(c => new { c.DocType, c.DocVersion }).ToListAsync(ct);
        return options.Value.Current
            .Select(d => new ConsentState(d.Type, d.Version, agreed.Any(a => a.DocType == d.Type && a.DocVersion == d.Version)))
            .ToList();
    }

    public async Task<bool> IsRequiredAsync(Guid userId, CancellationToken ct) =>
        (await StatusAsync(userId, ct)).Any(s => !s.Agreed);

    /// <summary>현재 버전과 다른 버전에 동의하려 하면 false (화면이 오래된 약관을 보여준 경우).</summary>
    public async Task<bool> AgreeAsync(Guid userId, IReadOnlyList<(ConsentDocType Type, string Version)> items, CancellationToken ct)
    {
        var current = options.Value.Current;
        if (items.Any(i => !current.Any(d => d.Type == i.Type && d.Version == i.Version))) return false;
        var now = time.GetUtcNow().UtcDateTime;
        foreach (var (type, version) in items.Distinct())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO user_consents ("UserId", "DocType", "DocVersion", "AgreedAt")
                VALUES ({userId}, {type.ToString()}, {version}, {now})
                ON CONFLICT ("UserId", "DocType", "DocVersion") DO NOTHING
                """, ct);
        }
        return true;
    }
}

public sealed record LoginResult(TokenPair Tokens, UserProfile User, bool IsNewUser, bool ConsentRequired);

/// <summary>OAuth 로그인 = 자동 가입 (SD-09). 식별은 (provider, subject) 만으로 한다.</summary>
public sealed class OAuthLoginService(
    MashilDbContext db,
    IdentityProviderRegistry providers,
    NonceService nonces,
    RefreshTokenService refreshTokens,
    ConsentService consents,
    SecretBox secrets,
    TimeProvider time,
    ILogger<OAuthLoginService> logger)
{
    public const int MaxDisplayNameLength = 20;

    public async Task<LoginResult> SignInAsync(
        AuthProvider provider, SocialCredential credential, string? nonce, string? device, string? displayNameHint, CancellationToken ct)
    {
        var verified = await providers.GetEnabled(provider).VerifyAsync(credential, nonce, ct);
        if (nonce is not null) await nonces.ConsumeAsync(nonce, ct);

        var now = time.GetUtcNow().UtcDateTime;
        var (user, isNew) = await FindOrCreateAsync(verified, displayNameHint, now, ct);
        user.LastLoginAt = now;
        await db.SaveChangesAsync(ct);

        var tokens = await refreshTokens.IssueAsync(user, device, ct);
        var consentRequired = await consents.IsRequiredAsync(user.Id, ct);
        logger.LogInformation("OAuth 로그인 {Provider} ({VerifiedBy}) user={User} new={New}", provider, verified.VerifiedBy, user.Id, isNew);
        return new LoginResult(tokens, user, isNew, consentRequired);
    }

    private async Task<(UserProfile User, bool IsNew)> FindOrCreateAsync(
        VerifiedIdentity v, string? displayNameHint, DateTime now, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            var identity = await db.UserIdentities
                .FirstOrDefaultAsync(i => i.Provider == v.Provider && i.ProviderSubject == v.Subject, ct);
            if (identity is not null)
            {
                var user = await db.UserProfiles.FirstAsync(u => u.Id == identity.UserId, ct);
                if (user.Status != UserStatus.Active) throw new AuthFailure("account_inactive", 403, "비활성 계정에 연결된 identity");
                identity.LastUsedAt = now;
                identity.ClientId = v.ClientId ?? identity.ClientId;
                if (v.Email is not null) identity.EmailAtProvider = v.Email;
                if (v.AppleRefreshToken is not null && secrets.IsAvailable)
                    identity.AppleRefreshTokenEnc = secrets.Seal(v.AppleRefreshToken);
                return (user, false);
            }

            var created = new UserProfile { DisplayName = CleanDisplayName(v.Nickname ?? displayNameHint), CreatedAt = now };
            db.UserProfiles.Add(created);
            db.UserIdentities.Add(new UserIdentity
            {
                UserId = created.Id,
                Provider = v.Provider,
                ProviderSubject = v.Subject,
                ClientId = v.ClientId,
                EmailAtProvider = v.Email,
                AppleRefreshTokenEnc = secrets.Seal(v.AppleRefreshToken),
                LinkedAt = now,
                LastUsedAt = now,
            });
            try
            {
                await db.SaveChangesAsync(ct);
                return (created, true);
            }
            catch (DbUpdateException ex) when (attempt == 0 && ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // 같은 계정으로 동시에 첫 로그인 → 먼저 만든 쪽을 쓴다
                db.ChangeTracker.Clear();
            }
        }
    }

    public static string? CleanDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var trimmed = string.Join(' ', name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return trimmed.Length <= MaxDisplayNameLength ? trimmed : trimmed[..MaxDisplayNameLength];
    }
}

/// <summary>
/// 계정 삭제 (USR-03, SD-08). 가입한 IdP 로 재인증 → 개인정보 파기 → IdP 연결 해제.
/// 연결 해제는 대기열에 먼저 넣고(같은 트랜잭션) 바로 시도한다. 실패해도 탈퇴는 끝나고 Worker 가 재시도한다.
/// </summary>
public sealed class AccountDeletionService(
    MashilDbContext db,
    IdentityProviderRegistry providers,
    NonceService nonces,
    IdpRevocationProcessor revocations,
    SecretBox secrets,
    TimeProvider time,
    ILogger<AccountDeletionService> logger)
{
    /// <param name="legacyPasswordCheck">IdP 연결이 없는 레거시(이메일) 계정의 재인증. null 이면 그런 계정은 삭제 불가.</param>
    public async Task DeleteAsync(
        Guid userId, AuthProvider? provider, SocialCredential? credential, string? nonce,
        Func<UserProfile, bool>? legacyPasswordCheck, CancellationToken ct)
    {
        var user = await db.UserProfiles.FirstOrDefaultAsync(u => u.Id == userId && u.Status == UserStatus.Active, ct)
                   ?? throw new AuthFailure("not_found", 404, "사용자 없음");
        var identities = await db.UserIdentities.Where(i => i.UserId == userId).ToListAsync(ct);

        VerifiedIdentity? fresh = null;
        if (identities.Count == 0)
        {
            if (legacyPasswordCheck is null || !legacyPasswordCheck(user))
                throw new AuthFailure("reauth_failed", 403, "레거시 계정 재인증 실패");
        }
        else
        {
            if (provider is null || credential is null) throw new AuthFailure("reauth_required", 400, "가입한 IdP 로 다시 로그인해야 함");
            var p = providers.Find(provider.Value) ?? throw new AuthFailure("provider_disabled", 404);
            fresh = await p.VerifyAsync(credential, nonce, ct);
            if (nonce is not null) await nonces.ConsumeAsync(nonce, ct);
            if (!identities.Any(i => i.Provider == fresh.Provider && i.ProviderSubject == fresh.Subject))
                throw new AuthFailure("identity_mismatch", 403, "재인증한 계정이 이 사용자와 연결돼 있지 않음");
        }

        var now = time.GetUtcNow().UtcDateTime;
        var queued = new List<(IdpRevocation Row, VerifiedIdentity? Fresh)>();
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            foreach (var i in identities)
            {
                var isFresh = fresh is not null && i.Provider == fresh.Provider && i.ProviderSubject == fresh.Subject;
                var row = new IdpRevocation
                {
                    Provider = i.Provider,
                    ProviderSubject = i.ProviderSubject,
                    ClientId = i.ClientId,
                    AppleRefreshTokenEnc = isFresh && fresh!.AppleRefreshToken is not null
                        ? secrets.Seal(fresh.AppleRefreshToken) ?? i.AppleRefreshTokenEnc
                        : i.AppleRefreshTokenEnc,
                    CreatedAt = now,
                    NextAttemptAt = now,
                };
                db.IdpRevocations.Add(row);
                queued.Add((row, isFresh ? fresh : null));
            }

            user.Email = null;
            user.DisplayName = null;
            user.PasswordHash = null;
            user.Status = UserStatus.Deleted;
            user.DeletedAt = now;
            await db.SaveChangesAsync(ct);

            await db.UserIdentities.Where(i => i.UserId == userId).ExecuteDeleteAsync(ct);
            await db.RefreshTokens.Where(t => t.UserId == userId).ExecuteDeleteAsync(ct);
            await db.UserBlocks.Where(b => b.BlockerId == userId || b.BlockedId == userId).ExecuteDeleteAsync(ct);
            // 댓글 평가(도움돼요/아니에요)는 개인 활동이라 함께 지운다 (댓글 자체는 "탈퇴한 사용자"로 남는다)
            await db.CommentVotes.Where(v => v.UserId == userId).ExecuteDeleteAsync(ct);
            // 평점·댓글·변경 요청·신고는 남기고 "탈퇴한 사용자"로 표시 (개인정보처리방침과 일치시킬 것)
            await tx.CommitAsync(ct);
        }
        logger.LogInformation("계정 삭제 user={User}, IdP 연결 해제 {Count}건 대기", userId, queued.Count);

        foreach (var (row, f) in queued)
        {
            try { await revocations.ProcessAsync(row, f, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                logger.LogWarning(ex, "IdP 연결 해제 즉시 시도 실패 — Worker 가 재시도 ({Provider})", row.Provider);
            }
        }
    }
}

/// <summary>IdP 연결 해제 대기열 처리. 성공하면 행을 지워 식별자를 남기지 않는다.</summary>
public sealed class IdpRevocationProcessor(
    MashilDbContext db,
    IdentityProviderRegistry providers,
    SecretBox secrets,
    TimeProvider time,
    ILogger<IdpRevocationProcessor> logger)
{
    public const int MaxAttempts = 10;

    public async Task<int> ProcessDueAsync(int max, CancellationToken ct)
    {
        var now = time.GetUtcNow().UtcDateTime;
        var due = await db.IdpRevocations
            .Where(r => r.Status == RevocationStatus.Pending && r.NextAttemptAt <= now)
            .OrderBy(r => r.NextAttemptAt).Take(max).ToListAsync(ct);
        foreach (var row in due)
        {
            try { await ProcessAsync(row, null, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                await RecordFailureAsync(row, ex.GetType().Name + ": " + ex.Message, TimeSpan.FromMinutes(5), ct);
            }
        }
        return due.Count;
    }

    public async Task ProcessAsync(IdpRevocation row, VerifiedIdentity? fresh, CancellationToken ct)
    {
        var provider = providers.Find(row.Provider);
        string? appleToken = null;
        try { appleToken = secrets.Open(row.AppleRefreshTokenEnc); }
        catch (System.Security.Cryptography.CryptographicException) { logger.LogWarning("Apple 토큰 복호화 실패 (키 변경?) revocation={Id}", row.Id); }

        var result = provider is null
            ? new RevokeResult(RevokeOutcome.NotConfigured, "제공자 없음")
            : await provider.RevokeAsync(new RevocationTarget(row.ProviderSubject, row.ClientId, appleToken), fresh, ct);

        if (result.Outcome == RevokeOutcome.Done)
        {
            db.IdpRevocations.Remove(row);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("IdP 연결 해제 완료 {Provider} revocation={Id}", row.Provider, row.Id);
            return;
        }
        var delay = result.Outcome == RevokeOutcome.NotConfigured
            ? TimeSpan.FromHours(6)
            : TimeSpan.FromMinutes(Math.Min(24 * 60, 5 * Math.Pow(2, row.Attempts)));
        await RecordFailureAsync(row, result.Error, delay, ct);
    }

    private async Task RecordFailureAsync(IdpRevocation row, string? error, TimeSpan delay, CancellationToken ct)
    {
        row.Attempts++;
        row.LastError = error is { Length: > 500 } ? error[..500] : error;
        row.NextAttemptAt = time.GetUtcNow().UtcDateTime + delay;
        if (row.Attempts >= MaxAttempts)
        {
            // 더 시도하지 않고 식별자·토큰을 지운다 (개인정보를 무기한 보관하지 않음)
            row.Status = RevocationStatus.Failed;
            row.ProviderSubject = null;
            row.AppleRefreshTokenEnc = null;
            logger.LogError("IdP 연결 해제 포기 {Provider} revocation={Id}: {Error}", row.Provider, row.Id, error);
        }
        else
        {
            logger.LogWarning("IdP 연결 해제 실패 {Provider} revocation={Id} 시도 {Attempts}: {Error}", row.Provider, row.Id, row.Attempts, error);
        }
        await db.SaveChangesAsync(ct);
    }
}
