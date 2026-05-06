namespace Mashil.Domain;

/// <summary>
/// 로컬 인증 사용자 레코드. 추후 Supabase 로 옮길 경우 <see cref="Id"/> 를 supabase.auth.users.id 와 매핑하고
/// <see cref="PasswordHash"/> 는 그대로 두거나 제거 (Supabase 가 자체적으로 비밀번호 보유) 하면 됨.
/// </summary>
public sealed class UserProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string Email { get; set; }

    /// <summary>
    /// ASP.NET Core <c>PasswordHasher&lt;UserProfile&gt;</c> 가 생성한 해시 (PBKDF2 + 솔트 포함).
    /// Supabase 마이그레이션 시 비워두고 OAuth subject 매핑으로 대체 가능.
    /// </summary>
    public required string PasswordHash { get; set; }

    public string? DisplayName { get; set; }
    public UserRole Role { get; set; } = UserRole.User;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
}

public enum UserRole
{
    User = 0,
    Admin = 1,
}
