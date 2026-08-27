using BistPriceService;
using Microsoft.Extensions.Options;
using Npgsql;

// İçerik kökünü exe klasörüne sabitle. Windows servisi olarak başlatılınca geçerli
// dizin C:\Windows\System32 olur; bu olmadan appsettings.json bulunamaz ve servis
// açılışta çöker (konsoldan çalışır çünkü geçerli dizin exe klasörüdür).
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
});

builder.Services.AddWindowsService(options =>
{
    // ServiceName '/' veya '\' içeremez (ServiceBase kısıtı). Bu ad yalnızca
    // ServiceBase/Event Log kaynağı içindir; SCM'deki kurulu servis adıyla
    // aynı olması gerekmez (own-process serviste ad yok sayılır).
    options.ServiceName = "BistUsFiyatServisi";
});

// Yapılandırma
builder.Services.Configure<PriceFetchOptions>(
    builder.Configuration.GetSection(PriceFetchOptions.SectionName));

// PostgreSQL bağlantı havuzu (uygulama ömrü boyunca tek NpgsqlDataSource).
var connectionString = builder.Configuration.GetConnectionString("BistTakip")
    ?? throw new InvalidOperationException("ConnectionStrings:BistTakip tanımlı değil.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));

builder.Services.AddSingleton<PriceRepository>();

// Yahoo Finance için HttpClient (User-Agent şart; aksi halde 429/403 dönebilir).
builder.Services.AddHttpClient<YahooFinanceClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<PriceFetchOptions>>().Value;
    client.BaseAddress = new Uri("https://query1.finance.yahoo.com/");
    client.Timeout = TimeSpan.FromSeconds(Math.Max(5, opts.RequestTimeoutSeconds));
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) BistPriceService/1.0");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});

// Her piyasa (BIST, US, ...) için: anlık fiyat + geçmiş + yeni-sembol dinleyicisi.
// NOT: AddHostedService(factory) içte TryAddEnumerable kullanır ve aynı implementasyon
// tipini (PriceWorker/HistoryBackfiller/...) tekilleştirir; bu yüzden her piyasa için
// ayrı örnek kaydedebilmek adına doğrudan AddSingleton<IHostedService> kullanıyoruz.
var settings = builder.Configuration.GetSection(PriceFetchOptions.SectionName).Get<PriceFetchOptions>() ?? new();
foreach (var market in settings.Markets)
{
    var m = market; // closure capture

    builder.Services.AddSingleton<IHostedService>(sp =>
        ActivatorUtilities.CreateInstance<PriceWorker>(sp, m));

    builder.Services.AddSingleton<IHostedService>(sp =>
        ActivatorUtilities.CreateInstance<HistoryBackfiller>(sp, m));

    builder.Services.AddSingleton<IHostedService>(sp =>
    {
        // Dinleyicinin kendi (aynı piyasa) geçmiş doldurucusu; durumsuz olduğu için
        // hosted örnekten ayrı olması sorun değil.
        var backfiller = ActivatorUtilities.CreateInstance<HistoryBackfiller>(sp, m);
        return ActivatorUtilities.CreateInstance<NewSymbolListener>(sp, m, backfiller);
    });
}

// USD/TRY: anlık kur + günlük kapanış geçmişi.
builder.Services.AddHostedService<FxWorker>();
builder.Services.AddHostedService<FxHistoryBackfiller>();

// Kıymetli maden (altın/gümüş) gram-TL anlık fiyatı.
builder.Services.AddHostedService<MetalWorker>();

// Döviz (EUR vb.) anlık fiyatı.
builder.Services.AddHostedService<CurrencyWorker>();

// Kripto (Binance) 7/24 anlık fiyatı.
builder.Services.AddHttpClient<BinanceClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<PriceFetchOptions>>().Value;
    client.BaseAddress = new Uri("https://api.binance.com/");
    client.Timeout = TimeSpan.FromSeconds(Math.Max(5, opts.RequestTimeoutSeconds));
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddHostedService<CryptoWorker>();

// TEFAS fonları (tefas.gov.tr resmi JSON API'si üzerinden) — günde birkaç kez.
builder.Services.AddHttpClient<FundClient>((sp, client) =>
{
    var opts = sp.GetRequiredService<IOptions<PriceFetchOptions>>().Value;
    client.BaseAddress = new Uri("https://www.tefas.gov.tr/");
    client.Timeout = TimeSpan.FromSeconds(Math.Max(5, opts.RequestTimeoutSeconds));
    client.DefaultRequestHeaders.UserAgent.ParseAdd(
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
    client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
});
builder.Services.AddHostedService<FundWorker>();
builder.Services.AddHostedService<NewFundListener>();

var host = builder.Build();
host.Run();
