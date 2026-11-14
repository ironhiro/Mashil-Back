
using Mashil.Import;
using Mashil.Import.LocalData;
using Mashil.Infrastructure;
using Mashil.Worker;
using Mashil.Worker.Geocoding;
using Mashil.Import.Geocoding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

// Mashil.Worker — API 와 분리된 백그라운드 작업 호스트 (OPS-01)
//   1. 일일 공중화장실 데이터 갱신 (DailyImportScheduler → DailyImportJob)
//   2. 주소 → 좌표 지오코딩 (GeocodeWorker)
// API 를 배포해도 이 작업들은 끊기지 않는다. 단일 인스턴스로 운영한다 (임포트는 advisory lock 으로도 보호).

var builder = Host.CreateApplicationBuilder(args);

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
