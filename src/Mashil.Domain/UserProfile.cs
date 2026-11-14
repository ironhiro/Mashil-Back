namespace Mashil.Domain;

/// <summary>
/// 마실 사용자. 식별은 <see cref="UserIdentity"/> 의 (provider, provider_subject) 로 한다 — 이메일은 식별에 쓰지 않는다 (USR-01).
/// 탈퇴하면 행은 남기고 개인정보를 비운 뒤 <see cref="Status"/> = Deleted (댓글은 "탈퇴한 사용자"로 표시, USR-03).
/// </summary>
public sealed class UserProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>선택. 식별에 쓰지 않고 탈퇴 시 파기. 레거시 이메일 로그인 사용자만 채워져 있다.</summary>
    public string? Email { get; set; }

    /// <summary>
    /// 레거시 이메일·비밀번호 로그인 전용 (개발 환경에서만 켜짐, <c>Auth:LegacyPasswordLogin</c>).
    /// OAuth 사용자는 null. 웹이 OAuth 로 옮겨 가면 컬럼째 삭제한다.
    /// </summary>
    public string? PasswordHash { get; set; }

    /// <summary>IdP 닉네임이 기본값. 사용자가 바꿀 수 있다. 탈퇴 시 파기.</summary>
    public string? DisplayName { get; set; }
    public UserRole Role { get; set; } = UserRole.User;
    public UserStatus Status { get; set; } = UserStatus.Active;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    /// <summary>탈퇴한 사용자의 표시 이름.</summary>
    public const string DeletedDisplayName = "탈퇴한 사용자";
}

public enum UserRole
{
    User = 0,
    Admin = 1,
}

public enum UserStatus
{
    Active = 0,
    Deleted = 1,
}
