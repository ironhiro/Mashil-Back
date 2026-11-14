using System.Text;
using Mashil.Api.Auth;
using Mashil.Api.Endpoints;
using Mashil.Domain;
using Mashil.Infrastructure;
using Mashil.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

try
{
    var builder = WebApplication.CreateBuilder(args);

    builder.Host.UseSerilog((ctx, lc) => lc
        .ReadFrom.Configuration(ctx.Configuration)
        .Enrich.FromLogContext()
        .WriteTo.Console());

    // --- DB ---
    var connStr = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");

    builder.Services.AddAppStatus(builder.Configuration); // 점검 모드 (/api/app/status)

    builder.Services.AddDbContext<MashilDbContext>(opt =>
        opt.UseNpgsql(connStr, npg => npg.UseNetTopologySuite()));

    // --- 인증: 마실 JWT (HS256) + OAuth 검증 서비스 (P4) ---
    builder.Services.AddSingleton(TimeProvider.System);
    builder.Services.AddMashilIdentity(builder.Configuration);
    var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
        ?? throw new InvalidOperationException("Jwt section missing");
    if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
        throw new InvalidOperationException("Jwt:SigningKey must be set (>= 32 chars).");

    // 레거시 이메일·비밀번호 로그인 — 웹이 OAuth 로 옮겨 갈 때까지 개발 환경에서만 (USR-01: 폐지)
    builder.Services.AddSingleton<IPasswordHasher<UserProfile>, PasswordHasher<UserProfile>>();

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(opts =>
        {
            opts.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                ValidIssuer = jwt.Issuer,
                ValidAudience = jwt.Audience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.FromSeconds(30),
            };
            opts.Events = new JwtBearerEvents
            {
                // 탈퇴한 사용자의 남은 Access Token 을 즉시 무효화 (만료까지 기다리지 않음)
                OnTokenValidated = async ctx =>
                {
                    var id = ctx.Principal?.TryGetUserId();
                    var db = ctx.HttpContext.RequestServices.GetRequiredService<MashilDbContext>();
                    var active = id is not null && await db.UserProfiles
                        .AnyAsync(u => u.Id == id && u.Status == UserStatus.Active, ctx.HttpContext.RequestAborted);
                    if (!active) ctx.Fail("user inactive");
                },
                // 클라이언트가 갱신할지 판단할 수 있게 만료를 알려 준다 (SD-11)
                OnChallenge = ctx =>
                {
                    if (ctx.AuthenticateFailure is SecurityTokenExpiredException)
                        ctx.Response.Headers["X-Auth-Error"] = "token_expired";
                    return Task.CompletedTask;
                },
            };
        });

    builder.Services.AddAuthorization(opt =>
    {
        opt.AddPolicy("Admin", p => p.RequireRole(UserRole.Admin.ToString()));
    });

    // --- JSON: enum 은 문자열로 주고받는다 (웹이 "Pending", "Update" 같은 문자열을 씀) ---
    builder.Services.ConfigureHttpJsonOptions(o =>
    {
        o.SerializerOptions.Converters.Add(new Mashil.Api.ChangeRequestKindJsonConverter()); // "Update" → Edit (G3)
        o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter());
    });

    // 지오코딩·일일 임포트는 Mashil.Worker 가 실행한다 (OPS-01). API 배포가 백그라운드 작업을 끊지 않도록 분리.

    // --- CORS (dev) ---
    // --- CORS: Cors:Origins (쉼표 구분). 기본은 vite dev 서버 ---
    var corsOrigins = (builder.Configuration["Cors:Origins"] ?? "http://localhost:5173")
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins(corsOrigins)
        .AllowAnyHeader()
        .AllowAnyMethod()));

    // --- OpenAPI ---
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(c =>
    {
        c.SwaggerDoc("v1", new OpenApiInfo
        {
            Title = "Mashil API",
            Version = "v1",
            Description = """
                **마실 (Mashil)** — 한국 공중·개방 화장실 데이터 기반 길찾기·평점·댓글 백엔드.

                ### 인증
                - `GET /api/auth/nonce` → IdP 로그인 → `POST /api/auth/oauth/{provider}` 로 마실 토큰 받기 (provider = kakao / google / apple)
                - Access Token 은 30분, `POST /api/auth/refresh` 로 회전 갱신
                - 개발 환경에서는 레거시 `POST /api/auth/register`·`/login` (이메일)도 열려 있다
                - 우상단 **Authorize** 버튼에 `Bearer <token>` 입력 (또는 토큰만 — Swagger UI 가 자동 prefix)
                - `RequireAuthorization` 표시된 endpoint 는 토큰 필수, `Admin` 표시는 `UserProfile.Role = Admin` 만 호출 가능

                ### 좌표/주소 정책
                Toilet 의 `Latitude`/`Longitude`/`Location` 과 `RoadAddress`/`LotAddress` 는 모두 nullable. CSV 임포트가 4-way 분류로 채우고, Geocode 워커가 누락분을 Kakao Local API 로 보충. `CoordinateSource`/`AddressSource` enum 으로 출처 추적.

                ### 길찾기
                현재 백엔드는 거리 계산만 제공 (`/api/toilets/near` 의 `distanceM`). 폴리라인 라우팅은 프런트에서 직선으로 그림 (Kakao Mobility 통합은 옵션).
                """,
            Contact = new OpenApiContact { Name = "Mashil 사이드 프로젝트" },
        });
        c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Description = "JWT Authorization 헤더. `/api/auth/login` 응답의 token 값을 그대로 입력 (Bearer prefix 자동).",
            Name = "Authorization",
            In = ParameterLocation.Header,
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
        });
        c.AddSecurityRequirement(new OpenApiSecurityRequirement
        {
            [new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" },
            }] = []
        });

        // Mashil.Api XML doc — endpoint Summary/Description, Request/Response record 설명
        var apiXml = Path.Combine(AppContext.BaseDirectory, "Mashil.Api.xml");
        if (File.Exists(apiXml)) c.IncludeXmlComments(apiXml, includeControllerXmlComments: true);
    });

    var app = builder.Build();

    if (app.Environment.IsDevelopment())
    {
        app.UseSwagger();
        app.UseSwaggerUI();
    }

    app.UseCors();
    app.UseAuthentication();
    app.UseAuthorization();

    // readiness: DB 까지 확인. 블루/그린 전환과 업타임 감시는 이쪽을 본다 (OPS-02)
    app.MapGet("/healthz/ready", async (MashilDbContext db, CancellationToken ct) =>
            await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable))
        .WithTags("Health").ExcludeFromDescription();

    app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
        .WithTags("Health")
        .WithSummary("간단 헬스체크")
        .WithDescription("프로세스/HTTP 파이프라인 살아있는지 확인용. DB 연결까지 검증하지는 않음.");

    app.MapAuthEndpoints();
    app.MapToiletEndpoints();
    app.MapAppEndpoints();
    app.MapInteractionEndpoints();
    app.MapModerationEndpoints();
    app.MapDevKakaoTestEndpoints(); // Development 에서만 매핑됨
    app.MapDevBridgeTestPage();     // Development 에서만 매핑됨 (Flutter 셸 브리지 시험)

    app.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Mashil.Api terminated unexpectedly");
}
finally
{
    Log.CloseAndFlush();
}
