using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Mashil.Domain;
using Mashil.Infrastructure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Mashil.Import.Tests;

/// <summary>테스트용 IdP: RSA 키로 ID Token 을 서명하고, 그 공개키를 JWKS 로 내준다.</summary>
internal sealed class FakeIdp : IConfigurationManager<OpenIdConnectConfiguration>
{
    public RsaSecurityKey Key { get; private set; } = NewKey("k1");
    private OpenIdConnectConfiguration _published;
    public int Refreshes { get; private set; }
    private RsaSecurityKey? _pendingRotation;

    public FakeIdp() => _published = Publish(Key);

    private static RsaSecurityKey NewKey(string kid) => new(RSA.Create(2048)) { KeyId = kid };

    private static OpenIdConnectConfiguration Publish(params SecurityKey[] keys)
    {
        var c = new OpenIdConnectConfiguration();
        foreach (var k in keys) c.SigningKeys.Add(k);
        return c;
    }

    /// <summary>IdP 가 키를 바꿨는데 우리 캐시는 아직 옛 JWKS 인 상황.</summary>
    public void RotateKeyWithoutRefreshingCache()
    {
        _pendingRotation = NewKey("k2");
        Key = _pendingRotation;
    }

    public Task<OpenIdConnectConfiguration> GetConfigurationAsync(CancellationToken cancel) => Task.FromResult(_published);

    public void RequestRefresh()
    {
        Refreshes++;
        if (_pendingRotation is not null) _published = Publish(_pendingRotation);
    }

    public string Token(string iss, string aud, string sub, IDictionary<string, object>? claims = null,
        DateTime? expires = null, SecurityKey? key = null, string alg = SecurityAlgorithms.RsaSha256)
    {
        var now = DateTime.UtcNow;
        var exp = expires ?? now.AddMinutes(10);
        var nbf = exp <= now ? exp.AddMinutes(-30) : now;
        var all = new Dictionary<string, object>(claims ?? new Dictionary<string, object>()) { ["sub"] = sub };
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = iss,
            Audience = aud,
            IssuedAt = nbf,
            NotBefore = nbf,
            Expires = exp,
            Claims = all,
            SigningCredentials = new SigningCredentials(key ?? Key, alg),
        });
    }
}

/// <summary>URL 별로 정해 둔 응답을 돌려주는 HTTP 스텁. 보낸 요청을 기록한다.</summary>
internal sealed class StubHttp : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpRequestMessage, (HttpStatusCode, string)>> _routes = new();
    public List<(HttpRequestMessage Request, string Body)> Sent { get; } = [];

    public StubHttp On(string url, Func<HttpRequestMessage, (HttpStatusCode, string)> respond)
    {
        _routes[url] = respond;
        return this;
    }

    public StubHttp On(string url, HttpStatusCode status, string body) => On(url, _ => (status, body));

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
        Sent.Add((request, body));
        var url = request.RequestUri!.GetLeftPart(UriPartial.Path);
        if (!_routes.TryGetValue(url, out var respond)) return new HttpResponseMessage(HttpStatusCode.NotFound);
        var (status, text) = respond(request);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    }
}

public class OidcTokenValidatorTests
{
    private const string Iss = "https://kauth.kakao.com";
    private readonly FakeIdp _idp = new();
    private OidcTokenValidator Validator => new(_idp, [Iss]);

    [Fact]
    public async Task Accepts_valid_token()
    {
        var t = await Validator.ValidateAsync(_idp.Token(Iss, "native-key", "12345"), ["native-key"], default);
        Assert.Equal("12345", t.Subject);
    }

    [Theory]
    [InlineData("https://evil.example", "native-key")]
    [InlineData(Iss, "other-app-key")]
    public async Task Rejects_wrong_issuer_or_audience(string iss, string aud)
    {
        var f = await Assert.ThrowsAsync<AuthFailure>(() => Validator.ValidateAsync(_idp.Token(iss, aud, "1"), ["native-key"], default));
        Assert.Equal("invalid_credential", f.Code);
    }

    [Fact]
    public async Task Rejects_expired_token()
    {
        var token = _idp.Token(Iss, "k", "1", expires: DateTime.UtcNow.AddMinutes(-10));
        await Assert.ThrowsAsync<AuthFailure>(() => Validator.ValidateAsync(token, ["k"], default));
    }

    [Fact]
    public async Task Rejects_token_signed_by_unknown_key()
    {
        var attacker = new RsaSecurityKey(RSA.Create(2048)) { KeyId = "k1" }; // 같은 kid, 다른 키
        var token = _idp.Token(Iss, "k", "1", key: attacker);
        await Assert.ThrowsAsync<AuthFailure>(() => Validator.ValidateAsync(token, ["k"], default));
    }

    [Fact]
    public async Task Rejects_hmac_token_even_with_matching_claims()
    {
        var hmac = new SymmetricSecurityKey(new byte[32]) { KeyId = "k1" };
        var token = _idp.Token(Iss, "k", "1", key: hmac, alg: SecurityAlgorithms.HmacSha256);
        await Assert.ThrowsAsync<AuthFailure>(() => Validator.ValidateAsync(token, ["k"], default));
    }

    [Fact]
    public async Task Refreshes_jwks_once_when_idp_rotated_keys()
    {
        _idp.RotateKeyWithoutRefreshingCache();
        var t = await Validator.ValidateAsync(_idp.Token(Iss, "k", "7"), ["k"], default);
        Assert.Equal("7", t.Subject);
        Assert.Equal(1, _idp.Refreshes);
    }

    [Fact]
    public async Task Disabled_when_no_audience_configured()
    {
        var f = await Assert.ThrowsAsync<AuthFailure>(() => Validator.ValidateAsync(_idp.Token(Iss, "k", "1"), [], default));
        Assert.Equal("provider_disabled", f.Code);
    }

    [Fact]
    public void Nonce_checks()
    {
        var withNonce = new JsonWebToken(_idp.Token(Iss, "k", "1", new Dictionary<string, object> { ["nonce"] = "abc" }));
        OidcTokenValidator.CheckNonce(withNonce, "abc", required: true);
        OidcTokenValidator.CheckNonce(withNonce, null, required: false);
        Assert.Equal("nonce_required", Assert.Throws<AuthFailure>(() => OidcTokenValidator.CheckNonce(withNonce, null, required: true)).Code);
        Assert.Throws<AuthFailure>(() => OidcTokenValidator.CheckNonce(withNonce, "other", required: false));

        var hashed = new JsonWebToken(_idp.Token(Iss, "k", "1", new Dictionary<string, object> { ["nonce"] = TokenHash.Sha256Hex("raw") }));
        OidcTokenValidator.CheckNonce(hashed, "raw", required: true, allowSha256: true);
        Assert.Throws<AuthFailure>(() => OidcTokenValidator.CheckNonce(hashed, "raw", required: true));

        var none = new JsonWebToken(_idp.Token(Iss, "k", "1"));
        Assert.Throws<AuthFailure>(() => OidcTokenValidator.CheckNonce(none, "abc", required: false));
    }
}

public class KakaoIdentityProviderTests
{
    private const string TokenInfo = "https://kapi.kakao.com/v1/user/access_token_info";
    private const string Me = "https://kapi.kakao.com/v2/user/me";
    private const string Unlink = "https://kapi.kakao.com/v1/user/unlink";
    private const string OAuthToken = "https://kauth.kakao.com/oauth/token";

    private readonly FakeIdp _idp = new();
    private readonly StubHttp _http = new();

    private KakaoIdentityProvider Provider(Action<KakaoAuthOptions>? configure = null)
    {
        var o = new KakaoAuthOptions { AppId = 1000, Audiences = ["native-key"] };
        configure?.Invoke(o);
        return new KakaoIdentityProvider(new HttpClient(_http), o, "rest-key",
            new OidcTokenValidator(_idp, [KakaoIdentityProvider.Issuer]), NullLogger<KakaoIdentityProvider>.Instance);
    }

    [Fact]
    public async Task Id_token_with_nonce_and_nickname()
    {
        var token = _idp.Token(KakaoIdentityProvider.Issuer, "native-key", "4242",
            new Dictionary<string, object> { ["nonce"] = "n1", ["nickname"] = "마실러" });
        var v = await Provider().VerifyAsync(new SocialCredential(IdToken: token), "n1", default);
        Assert.Equal(("4242", "마실러", "native-key"), (v.Subject, v.Nickname, v.ClientId));
        Assert.Null(v.KakaoAccessToken);
        Assert.Empty(_http.Sent);
    }

    [Fact]
    public async Task Access_token_of_another_app_is_rejected()
    {
        _http.On(TokenInfo, HttpStatusCode.OK, """{"id":4242,"expires_in":7000,"app_id":9999}""");
        var f = await Assert.ThrowsAsync<AuthFailure>(() =>
            Provider().VerifyAsync(new SocialCredential(AccessToken: "at"), null, default));
        Assert.Equal("invalid_credential", f.Code);
        Assert.DoesNotContain(_http.Sent, s => s.Request.RequestUri!.ToString().StartsWith(Me));
    }

    [Fact]
    public async Task Access_token_of_our_app_reads_profile()
    {
        _http.On(TokenInfo, HttpStatusCode.OK, """{"id":4242,"expires_in":7000,"app_id":1000}""")
             .On(Me, HttpStatusCode.OK, """{"id":4242,"kakao_account":{"profile":{"nickname":"닉"},"email":"a@b.c","is_email_valid":true,"is_email_verified":false}}""");
        var v = await Provider().VerifyAsync(new SocialCredential(AccessToken: "at"), null, default);
        Assert.Equal(("4242", "닉"), (v.Subject, v.Nickname));
        Assert.Null(v.Email); // 인증 안 된 이메일은 받지 않음
        Assert.Equal("at", v.KakaoAccessToken);
        Assert.All(_http.Sent, s => Assert.Equal("Bearer", s.Request.Headers.Authorization!.Scheme));
    }

    [Fact]
    public async Task Access_token_flow_refused_when_nonce_required()
    {
        var f = await Assert.ThrowsAsync<AuthFailure>(() =>
            Provider(o => o.RequireNonce = true).VerifyAsync(new SocialCredential(AccessToken: "at"), null, default));
        Assert.Equal("nonce_required", f.Code);
    }

    [Fact]
    public async Task Extra_access_token_is_kept_only_if_it_belongs_to_same_user()
    {
        _http.On(TokenInfo, HttpStatusCode.OK, """{"id":1,"expires_in":7000,"app_id":1000}""");
        var token = _idp.Token(KakaoIdentityProvider.Issuer, "native-key", "4242");
        var v = await Provider().VerifyAsync(new SocialCredential(IdToken: token, AccessToken: "someone-else"), null, default);
        Assert.Null(v.KakaoAccessToken);
    }

    [Fact]
    public async Task Authorization_code_requires_registered_redirect_uri()
    {
        var p = Provider(o => o.RedirectUris = ["https://mashil.app/auth/kakao"]);
        var f = await Assert.ThrowsAsync<AuthFailure>(() =>
            p.VerifyAsync(new SocialCredential(AuthorizationCode: "c", RedirectUri: "https://evil.example/cb"), null, default));
        Assert.Equal("invalid_request", f.Code);

        var idToken = _idp.Token(KakaoIdentityProvider.Issuer, "rest-key", "77");
        _http.On(OAuthToken, HttpStatusCode.OK, $$"""{"access_token":"at","id_token":"{{idToken}}"}""")
             .On(TokenInfo, HttpStatusCode.OK, """{"id":77,"expires_in":7000,"app_id":1000}""");
        var v = await Provider(o => { o.RedirectUris = ["https://mashil.app/auth/kakao"]; o.Audiences = ["native-key", "rest-key"]; })
            .VerifyAsync(new SocialCredential(AuthorizationCode: "c", RedirectUri: "https://mashil.app/auth/kakao"), null, default);
        Assert.Equal("77", v.Subject);
        Assert.Contains("client_id=rest-key", _http.Sent.First(s => s.Request.RequestUri!.ToString() == OAuthToken).Body);
    }

    [Fact]
    public async Task Unlink_prefers_fresh_user_token_then_admin_key()
    {
        _http.On(Unlink, r => r.Headers.Authorization!.Scheme == "Bearer"
            ? (HttpStatusCode.Unauthorized, """{"code":-401}""")
            : (HttpStatusCode.OK, """{"id":4242}"""));
        var fresh = new VerifiedIdentity(AuthProvider.Kakao, "4242", null, null, null) { KakaoAccessToken = "at" };
        var r = await Provider(o => o.AdminKey = "admin").RevokeAsync(new RevocationTarget("4242", null, null), fresh, default);
        Assert.Equal(RevokeOutcome.Done, r.Outcome);
        Assert.Equal(["Bearer", "KakaoAK"], _http.Sent.Select(s => s.Request.Headers.Authorization!.Scheme));
        Assert.Contains("target_id=4242", _http.Sent[1].Body);
    }

    [Fact]
    public async Task Unlink_without_admin_key_is_not_configured_and_already_unlinked_is_done()
    {
        var none = await Provider().RevokeAsync(new RevocationTarget("4242", null, null), null, default);
        Assert.Equal(RevokeOutcome.NotConfigured, none.Outcome);

        _http.On(Unlink, HttpStatusCode.BadRequest, """{"msg":"NotRegisteredUserException","code":-101}""");
        var already = await Provider(o => o.AdminKey = "admin").RevokeAsync(new RevocationTarget("4242", null, null), null, default);
        Assert.Equal(RevokeOutcome.Done, already.Outcome);

        _http.On(Unlink, HttpStatusCode.InternalServerError, "oops");
        var retry = await Provider(o => o.AdminKey = "admin").RevokeAsync(new RevocationTarget("4242", null, null), null, default);
        Assert.Equal(RevokeOutcome.Retry, retry.Outcome);
    }
}

public class AppleAndCryptoTests
{
    [Fact]
    public async Task Apple_accepts_hashed_nonce_and_requires_nonce_by_default()
    {
        var idp = new FakeIdp();
        var apple = new AppleIdentityProvider(new HttpClient(new StubHttp()), new AppleAuthOptions { ClientIds = ["app.mashil"] },
            new OidcTokenValidator(idp, [AppleIdentityProvider.Issuer]), TimeProvider.System, NullLogger<AppleIdentityProvider>.Instance);
        var token = idp.Token(AppleIdentityProvider.Issuer, "app.mashil", "001.abc",
            new Dictionary<string, object> { ["nonce"] = TokenHash.Sha256Hex("raw"), ["email"] = "x@privaterelay.appleid.com" });

        var v = await apple.VerifyAsync(new SocialCredential(IdToken: token), "raw", default);
        Assert.Equal(("001.abc", "app.mashil"), (v.Subject, v.ClientId));

        var f = await Assert.ThrowsAsync<AuthFailure>(() => apple.VerifyAsync(new SocialCredential(IdToken: token), null, default));
        Assert.Equal("nonce_required", f.Code);
    }

    [Fact]
    public async Task Apple_client_secret_is_es256_jwt_for_team_and_client()
    {
        using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = ec.ExportPkcs8PrivateKeyPem();
        var apple = new AppleIdentityProvider(new HttpClient(new StubHttp()),
            new AppleAuthOptions { ClientIds = ["app.mashil"], TeamId = "TEAM123", KeyId = "KEY456", PrivateKey = pem },
            new OidcTokenValidator(new FakeIdp(), [AppleIdentityProvider.Issuer]), TimeProvider.System, NullLogger<AppleIdentityProvider>.Instance);

        var secret1 = apple.CreateClientSecret("app.mashil");
        var secret2 = apple.CreateClientSecret("app.mashil"); // 두 번째 호출도 (dispose 된 키 캐시 없이) 동작

        var result = await new JsonWebTokenHandler().ValidateTokenAsync(secret2, new TokenValidationParameters
        {
            ValidIssuer = "TEAM123",
            ValidAudience = "https://appleid.apple.com",
            IssuerSigningKey = new ECDsaSecurityKey(ec),
        });
        Assert.True(result.IsValid, result.Exception?.Message);
        var jwt = new JsonWebToken(secret1);
        Assert.Equal(("ES256", "KEY456", "app.mashil"), (jwt.Alg, jwt.Kid, jwt.Subject));
    }

    [Fact]
    public void SecretBox_roundtrip_and_tamper_detection()
    {
        var box = new SecretBox(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        var sealedBytes = box.Seal("r.apple.refresh")!;
        Assert.Equal("r.apple.refresh", box.Open(sealedBytes));
        sealedBytes[^1] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => box.Open(sealedBytes));

        var off = new SecretBox(null);
        Assert.False(off.IsAvailable);
        Assert.Null(off.Seal("x"));
        Assert.Throws<InvalidOperationException>(() => new SecretBox(Convert.ToBase64String(new byte[16])));
    }

    [Fact]
    public void Blank_env_values_do_not_enable_providers()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["OAuth:Kakao:AppId"] = "",
            ["OAuth:Kakao:Audiences:0"] = "",
            ["OAuth:Google:ClientIds:0"] = " ",
            ["OAuth:Apple:ClientIds:0"] = "app.mashil",
        }).Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(TimeProvider.System)
            .AddMashilIdentity(config);
        using var sp = services.BuildServiceProvider();
        var registry = sp.GetRequiredService<IdentityProviderRegistry>();
        Assert.Equal([AuthProvider.Apple], registry.EnabledProviders);
    }

    [Theory]
    [InlineData("  마실   러  ", "마실 러")]
    [InlineData("   ", null)]
    [InlineData(null, null)]
    [InlineData("아주아주아주아주아주아주아주아주긴닉네임입니다", "아주아주아주아주아주아주아주아주긴닉네임")]
    public void Display_name_is_cleaned(string? input, string? expected) =>
        Assert.Equal(expected, OAuthLoginService.CleanDisplayName(input));
}
