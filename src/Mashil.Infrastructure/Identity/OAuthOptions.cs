namespace Mashil.Infrastructure.Identity;

/// <summary>
/// 설정 섹션 <c>OAuth</c>. 키·시크릿은 User Secrets / 환경 변수로만 넣는다 (appsettings 에 적지 않음).
/// 제공자는 필요한 값이 채워졌을 때만 켜진다 — 비어 있으면 그 제공자로 로그인하면 404 (provider_disabled).
/// </summary>
public sealed class OAuthOptions
{
    public const string Section = "OAuth";

    public KakaoAuthOptions Kakao { get; set; } = new();
    public GoogleAuthOptions Google { get; set; } = new();
    public AppleAuthOptions Apple { get; set; } = new();

    /// <summary>Apple refresh token 암호화 키 (base64, 32바이트). 없으면 Apple 토큰을 저장하지 않는다.</summary>
    public string? TokenEncryptionKey { get; set; }

    public int RefreshTokenDays { get; set; } = 30;
    public int NonceMinutes { get; set; } = 5;
}

public sealed class KakaoAuthOptions
{
    /// <summary>앱 ID (숫자). Access Token 로그인 시 토큰이 우리 앱 것인지 확인한다 (access_token_info.app_id).</summary>
    public long? AppId { get; set; }

    /// <summary>ID Token 의 aud 로 허용할 앱 키 — 네이티브 앱 키, JavaScript 키, REST API 키 (로그인한 플랫폼의 키가 aud 가 된다).</summary>
    public string[] Audiences { get; set; } = [];

    /// <summary>웹 인가 코드 교환용. 비우면 <c>Kakao:RestApiKey</c>(지오코딩과 같은 키)를 쓴다.</summary>
    public string? RestApiKey { get; set; }

    /// <summary>카카오 로그인 > 보안 > Client Secret 을 켰을 때만.</summary>
    public string? ClientSecret { get; set; }

    /// <summary>탈퇴 시 연결 끊기(unlink)용 Admin 키. 서버에만 둔다. 없으면 재인증 때 받은 사용자 토큰으로만 끊을 수 있다.</summary>
    public string? AdminKey { get; set; }

    /// <summary>인가 코드 교환을 허용할 redirect_uri (웹 로그인, USR-04). 비우면 인가 코드 방식은 꺼진다.</summary>
    public string[] RedirectUris { get; set; } = [];

    /// <summary>true 면 nonce 없는 요청을 거부한다 (Access Token 단독 방식은 nonce 를 담을 수 없어 함께 거부됨).</summary>
    public bool RequireNonce { get; set; }

    public bool Enabled => AppId is not null || Audiences.Length > 0;
}

public sealed class GoogleAuthOptions
{
    /// <summary>ID Token aud 로 허용할 OAuth 클라이언트 ID (웹, iOS, Android 의 serverClientId 로 쓴 웹 클라이언트).</summary>
    public string[] ClientIds { get; set; } = [];
    public bool RequireNonce { get; set; }
    public bool Enabled => ClientIds.Length > 0;
}

public sealed class AppleAuthOptions
{
    /// <summary>ID Token aud 로 허용할 값 — iOS 번들 ID, 웹/Android 용 Services ID.</summary>
    public string[] ClientIds { get; set; } = [];
    public string? TeamId { get; set; }
    public string? KeyId { get; set; }

    /// <summary>Sign in with Apple 키(.p8) PEM 전체. client_secret(ES256) 서명용.</summary>
    public string? PrivateKey { get; set; }

    /// <summary>Apple 은 nonce 를 기본으로 요구한다 (리플레이 방지).</summary>
    public bool RequireNonce { get; set; } = true;

    public bool Enabled => ClientIds.Length > 0;
    public bool CanCallTokenEndpoint =>
        !string.IsNullOrWhiteSpace(TeamId) && !string.IsNullOrWhiteSpace(KeyId) && !string.IsNullOrWhiteSpace(PrivateKey);
}

/// <summary>마실 자체 Access JWT. 섹션 <c>Jwt</c>.</summary>
public sealed class JwtOptions
{
    public string SigningKey { get; set; } = "";
    public string Issuer { get; set; } = "mashil-api";
    public string Audience { get; set; } = "mashil-web";

    /// <summary>Access Token 만료. 짧게 두고 Refresh Token 으로 갱신한다 (USR-02).</summary>
    public int ExpiresMinutes { get; set; } = 30;
}

/// <summary>섹션 <c>Consents</c>. 문서 버전을 올리면 다음 로그인 때 다시 동의를 받는다.</summary>
public sealed class ConsentOptions
{
    public const string Section = "Consents";

    /// <summary>
    /// 설정에 적은 현재 문서들. 초기값은 비워 둔다 — 설정 바인더는 배열 초기값 뒤에 설정 항목을 덧붙이므로
    /// 기본값을 여기 두면 같은 문서가 두 번(코드 v1 + 설정 vN) 들어간다 (#15). 실제로 쓰는 목록은 <see cref="Current"/>.
    /// </summary>
    public ConsentDocument[] Documents { get; set; } = [];

    /// <summary>설정에 문서가 없을 때 쓰는 기본 문서 (각 v1).</summary>
    public static IReadOnlyList<ConsentDocument> DefaultDocuments { get; } =
    [
        new() { Type = Domain.ConsentDocType.Terms, Version = "1" },
        new() { Type = Domain.ConsentDocType.Privacy, Version = "1" },
        new() { Type = Domain.ConsentDocType.LocationTerms, Version = "1" },
    ];

    /// <summary>현재 동의 대상 문서. 설정이 비었으면 기본 문서.</summary>
    public IReadOnlyList<ConsentDocument> Current => Documents.Length > 0 ? Documents : DefaultDocuments;

    /// <summary>true 면 동의 전에는 쓰기(평점·댓글·변경 요청·신고)를 403 consent_required 로 막는다.</summary>
    public bool EnforceOnWrite { get; set; } = true;
}

public sealed class ConsentDocument
{
    public Domain.ConsentDocType Type { get; set; }
    public string Version { get; set; } = "1";
}

/// <summary>섹션 <c>Moderation</c>.</summary>
public sealed class ModerationOptions
{
    public const string Section = "Moderation";

    /// <summary>서로 다른 사용자의 신고가 이 수에 이르면 관리자 확인 전까지 자동 숨김. 0 이면 끔.</summary>
    public int AutoHideReportCount { get; set; } = 3;
}
