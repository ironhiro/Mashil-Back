namespace Mashil.Domain;

public enum AuthProvider
{
    Google = 0,
    Kakao = 1,
    Apple = 2,
}

/// <summary>소셜 계정 연결. (Provider, ProviderSubject) 하나에 마실 사용자 1명. 탈퇴 시 행 삭제.</summary>
public sealed class UserIdentity
{
    public long Id { get; set; }
    public required Guid UserId { get; set; }
    public AuthProvider Provider { get; set; }

    /// <summary>IdP 의 sub (Kakao 는 회원번호).</summary>
    public required string ProviderSubject { get; set; }

    /// <summary>토큰의 aud — 어느 앱 키/클라이언트로 로그인했는지. Apple revoke 의 client_id 로 쓴다.</summary>
    public string? ClientId { get; set; }

    /// <summary>IdP 가 준 이메일 (선택 동의, Apple 은 릴레이 주소일 수 있음). 식별에 쓰지 않는다.</summary>
    public string? EmailAtProvider { get; set; }

    /// <summary>Apple revoke 용 refresh token. AES-GCM 으로 암호화해 저장 (원문 저장 안 함).</summary>
    public byte[]? AppleRefreshTokenEnc { get; set; }

    public DateTime LinkedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastUsedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>Refresh Token. 원문은 저장하지 않고 SHA-256 만. 회전할 때마다 같은 FamilyId 로 새 행.</summary>
public sealed class RefreshToken
{
    public long Id { get; set; }
    public required Guid UserId { get; set; }
    public required string TokenHash { get; set; }
    public required string FamilyId { get; set; }
    public string? DeviceLabel { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
    public DateTime? RevokedAt { get; set; }
}

/// <summary>OAuth 요청에 묶는 1회용 nonce (5분). 인스턴스가 여러 대여도 동작하도록 DB 에 둔다.</summary>
public sealed class AuthNonce
{
    public long Id { get; set; }
    public required string NonceHash { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? ConsumedAt { get; set; }
}

public enum ConsentDocType
{
    Terms = 0,
    Privacy = 1,
    LocationTerms = 2,
}

/// <summary>약관 동의 이력 (버전별). 탈퇴 후에도 동의 증빙으로 보관 — 개인정보는 user_id 뿐 (LEG-03).</summary>
public sealed class UserConsent
{
    public long Id { get; set; }
    public required Guid UserId { get; set; }
    public ConsentDocType DocType { get; set; }
    public required string DocVersion { get; set; }
    public DateTime AgreedAt { get; set; } = DateTime.UtcNow;
}

public enum RevocationStatus
{
    Pending = 0,
    Failed = 1,
}

/// <summary>
/// 탈퇴 시 IdP 연결 해제(Kakao unlink, Apple revoke) 대기열. 성공하면 행을 지운다.
/// 실패해도 탈퇴는 막지 않고 Worker 가 재시도한다 (SD-08). 최대 횟수를 넘기면 식별자를 지우고 Failed 로 남긴다.
/// </summary>
public sealed class IdpRevocation
{
    public long Id { get; set; }
    public AuthProvider Provider { get; set; }
    public string? ProviderSubject { get; set; }
    public string? ClientId { get; set; }
    public byte[]? AppleRefreshTokenEnc { get; set; }
    public RevocationStatus Status { get; set; } = RevocationStatus.Pending;
    public int Attempts { get; set; }
    public string? LastError { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
}
