using System.Net.Http.Headers;
using System.Text;
using Mashil.Api.Auth;
using Mashil.Api.Background;
using Mashil.Api.Endpoints;
using Mashil.Api.External;
using Mashil.Domain;
using Mashil.Infrastructure;
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

    builder.Services.AddDbContext<MashilDbContext>(opt =>
        opt.UseNpgsql(connStr, npg => npg.UseNetTopologySuite()));

    // --- Auth (local JWT) ---
    builder.Services.Configure<JwtOptions>(builder.Configuration.GetSection("Jwt"));
    var jwt = builder.Configuration.GetSection("Jwt").Get<JwtOptions>()
        ?? throw new InvalidOperationException("Jwt section missing");
    if (string.IsNullOrWhiteSpace(jwt.SigningKey) || jwt.SigningKey.Length < 32)
        throw new InvalidOperationException("Jwt:SigningKey must be set (>= 32 chars).");

    builder.Services.AddSingleton<JwtTokenService>();
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
                ClockSkew = TimeSpan.FromSeconds(30),
            };
        });

    builder.Services.AddAuthorization(opt =>
    {
        opt.AddPolicy("Admin", p => p.RequireRole(UserRole.Admin.ToString()));
    });

    // --- YhLibrary HTTP client (Kakao Local API geocoding 등) ---
    builder.Services.AddSingleton<YhLibrary.Serialization.ISerializer, YhLibrary.Serialization.JsonSerializer>();
    builder.Services.AddHttpClient<YhLibrary.Http.ApiClient>((sp, c) =>
    {
        c.BaseAddress = new Uri("https://dapi.kakao.com");
        var key = sp.GetRequiredService<IConfiguration>()["Kakao:RestApiKey"];
        if (!string.IsNullOrWhiteSpace(key))
        {
            // Kakao 는 'Authorization: KakaoAK <key>' 형식 — Bearer 가 아님.
            c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("KakaoAK", key);
        }
    });
    builder.Services.AddScoped<KakaoLocalClient>();

    // --- Geocode 백그라운드 워커 ---
    builder.Services.Configure<GeocodeWorkerOptions>(builder.Configuration.GetSection("Geocode"));
    builder.Services.AddHostedService<GeocodeWorker>();

    // --- CORS (dev) ---
    builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
        .WithOrigins("http://localhost:5173")
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
                - `POST /api/auth/register` 또는 `/login` 으로 JWT 받기
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

    app.MapGet("/healthz", () => Results.Ok(new { status = "ok" }))
        .WithTags("Health")
        .WithSummary("간단 헬스체크")
        .WithDescription("프로세스/HTTP 파이프라인 살아있는지 확인용. DB 연결까지 검증하지는 않음.");

    app.MapAuthEndpoints();
    app.MapToiletEndpoints();
    app.MapInteractionEndpoints();

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
