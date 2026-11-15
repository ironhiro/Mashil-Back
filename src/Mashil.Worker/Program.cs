using Mashil.Import;
using Mashil.Import.LocalData;
using Mashil.Infrastructure;
using Mashil.Infrastructure.Identity;
using Mashil.Worker;
using Mashil.Worker.Geocoding;
using Mashil.Import.Geocoding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Sentry;
using Sentry.Serilog;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

// Mashil.Worker — API 와 분리된 백그라운드 작업 호스트 (OPS-01)
//   1. 일일 공중화장실 데이터 갱신 (DailyImportScheduler → DailyImportJob)
//   2. 주소 → 좌표 지오코딩 (GeocodeWorker)
//   3. 탈퇴 사용자 IdP 연결 해제 재시도 (IdpRevocationWorker)
// API 를 배포해도 이 작업들은 끊기지 않는다. 단일 인스턴스로 운영한다 (임포트는 advisory lock 으로도 보호).

Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Information()
    .Enrich.FromLogContext()
    .WriteTo.Console()
    .CreateBootstrapLogger();

IDisposable? sentry = null;
try
{
    var builder = Host.CreateApplicationBuilder(args);

    // --- 로그: API 와 같은 Serilog 설정 (Serilog:MinimumLevel). 운영은 콘솔 JSON 한 줄씩, 개발은 텍스트 ---
    // 에러 모니터링 (NFR-10): Sentry:Dsn 이 있을 때만 Error 이상을 Sentry 로. 개인정보는 보내지 않는다.
    sentry = InitSentry(builder);
    builder.Services.AddSerilog((_, lc) =>
    {
        lc.ReadFrom.Configuration(builder.Configuration).Enrich.FromLogContext();
        if (builder.Environment.IsDevelopment()) lc.WriteTo.Console();
        else lc.WriteTo.Console(new RenderedCompactJsonFormatter());
        if (sentry is not null)
            lc.WriteTo.Sentry(o =>
            {
                o.MinimumEventLevel = LogEventLevel.Error;
                o.MinimumBreadcrumbLevel = LogEventLevel.Information;
            });
    });

    var connStr = builder.Configuration.GetConnectionString("Postgres")
        ?? throw new InvalidOperationException("ConnectionStrings:Postgres is not configured.");
    builder.Services.AddDbContext<MashilDbContext>(opt => opt.UseNpgsql(connStr, npg => npg.UseNetTopologySuite()));

    builder.Services.AddSingleton(TimeProvider.System);

    // --- 일일 임포트 ---
    builder.Services.Configure<ImportScheduleOptions>(builder.Configuration.GetSection("Import:Schedule"));
    builder.Services.AddSingleton(builder.Configuration.GetSection("Import:Policy").Get<ImportPolicy>() ?? ImportPolicy.Default);
    builder.Services.AddSingleton(builder.Configuration.GetSection("Import:LocalData").Get<LocalDataFetchOptions>() ?? new LocalDataFetchOptions());
    builder.Services.AddSingleton(sp => new LocalDataFileFetcher(sp.GetRequiredService<LocalDataFetchOptions>()));
    builder.Services.AddScoped<DailyImportJob>();
    builder.Services.AddHostedService<DailyImportScheduler>();

    // --- 알림: 로그는 항상, Alerts:WebhookUrl 이 있으면 웹훅도 ---
    builder.Services.AddHttpClient(nameof(WebhookAlertNotifier));
    builder.Services.AddSingleton<IAlertNotifier>(sp =>
    {
        var notifiers = new List<IAlertNotifier> { ActivatorUtilities.CreateInstance<LoggingAlertNotifier>(sp) };
        var url = sp.GetRequiredService<IConfiguration>()["Alerts:WebhookUrl"];
        if (Uri.TryCreate(url, UriKind.Absolute, out var webhook))
        {
            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(WebhookAlertNotifier));
            notifiers.Add(ActivatorUtilities.CreateInstance<WebhookAlertNotifier>(sp, http, webhook));
        }
        return new CompositeAlertNotifier(notifiers);
    });

    // --- 지오코딩 (Kakao Local API) — 키가 없으면 GeocodingService 를 등록하지 않음 (워커가 idle) ---
    builder.Services.Configure<GeocodeWorkerOptions>(builder.Configuration.GetSection("Geocode"));
    builder.Services.AddHttpClient(nameof(KakaoAddressGeocoder), c => c.Timeout = TimeSpan.FromSeconds(10));
    var kakaoKey = builder.Configuration["Kakao:RestApiKey"];
    if (!string.IsNullOrWhiteSpace(kakaoKey))
    {
        builder.Services.AddScoped<IAddressGeocoder>(sp => new KakaoAddressGeocoder(
            sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(KakaoAddressGeocoder)), kakaoKey));
        builder.Services.AddScoped(sp =>
        {
            var o = sp.GetRequiredService<IOptions<GeocodeWorkerOptions>>().Value;
            return new GeocodingService(sp.GetRequiredService<MashilDbContext>(), sp.GetRequiredService<IAddressGeocoder>(),
                new GeocodingOptions { BatchSize = o.BatchSize, RequestDelayMs = o.RequestDelayMs, DailyBudget = o.DailyBudget },
                sp.GetRequiredService<TimeProvider>());
        });
    }
    builder.Services.AddHostedService<GeocodeWorker>();

    // --- 탈퇴 IdP 연결 해제 재시도 — OAuth:Kakao:AdminKey, OAuth:Apple:* 를 API 와 같은 값으로 ---
    builder.Services.AddMashilIdentity(builder.Configuration);
    builder.Services.AddHostedService<IdpRevocationWorker>();

    var host = builder.Build();

    // 스키마는 배포 파이프라인이 적용한다 (OPS-03). 미적용이면 시작하지 않음.
    using (var scope = host.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<MashilDbContext>();
        var pending = (await db.Database.GetPendingMigrationsAsync()).ToList();
        if (pending.Count > 0)
            throw new InvalidOperationException($"적용되지 않은 마이그레이션이 있습니다: {string.Join(", ", pending)}");
    }

    await host.RunAsync();
    return 0;
}
catch (Exception ex)
{
    Log.Fatal(ex, "Mashil.Worker terminated unexpectedly");
    return 1;
}
finally
{
    await Log.CloseAndFlushAsync();
    sentry?.Dispose(); // 남은 이벤트 전송
}

static IDisposable? InitSentry(HostApplicationBuilder b)
{
    var dsn = b.Configuration["Sentry:Dsn"];
    // 잘못 적은 DSN 으로 워커가 죽지 않게 모양만 확인 (API 의 ErrorMonitoring.IsValidDsn 과 같은 규칙)
    if (string.IsNullOrWhiteSpace(dsn) || !Uri.TryCreate(dsn.Trim(), UriKind.Absolute, out var u)
        || u.Scheme is not ("https" or "http") || string.IsNullOrEmpty(u.UserInfo) || u.AbsolutePath.Trim('/').Length == 0) return null;
    return SentrySdk.Init(o =>
    {
        o.Dsn = dsn.Trim();
        o.SendDefaultPii = false;
        // 통신에 끼어들지 않게 (API 의 ErrorMonitoring.KeepOutOfTraffic 과 같음): 외부 API(Kakao 지오코딩, LOCALDATA, IdP 해제)
        // 요청에 sentry-trace·baggage 헤더를 붙이지 않고 HttpClient 를 가로채지 않는다
        o.TracePropagationTargets = [];
        o.PropagateTraceparent = false;
        o.DisableSentryHttpMessageHandler = true;
        o.TracesSampleRate = 0;
        o.Environment = b.Configuration["Sentry:Environment"] ?? b.Environment.EnvironmentName.ToLowerInvariant();
        // Release 는 SDK 가 SENTRY_RELEASE 환경 변수(이미지 빌드 때 git sha)를 읽는다
        o.UseSerilog();
    });
}
