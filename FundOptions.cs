namespace BistPriceService;

/// <summary>
/// TEFAS fon fiyatı ayarları. fund_prices tablosundaki kodlar (izlenen fonlar) okunur,
/// her biri hangikredi.com üzerinden çekilip aynı tabloya yazılır. TEFAS fiyatları
/// günde bir açıklandığı için günde birkaç kez taramak yeterli.
/// </summary>
public sealed class FundOptions
{
    /// <summary>Fon fiyat tablosu (PK code; title, price, updated_at).</summary>
    public string Table { get; set; } = "fund_prices";

    /// <summary>Günlük tam tarama saati "HH:mm" (TEFAS gecikmeli açıkladığı için sabah).</summary>
    public string DailyRunTime { get; set; } = "09:30";

    /// <summary>Günlük tarama saatinin saat dilimi.</summary>
    public string TimeZoneId { get; set; } = "Turkey Standard Time";

    /// <summary>Servis açılışında da bir tam tarama yapılsın mı? (sonra her gün DailyRunTime).</summary>
    public bool RunOnStartup { get; set; } = true;

    /// <summary>Fonlar arası bekleme (ms) — kaynağı yormamak için.</summary>
    public int PerFundDelayMs { get; set; } = 500;

    /// <summary>Yeni fon eklenince tetiklenecek NOTIFY kanalı.</summary>
    public string NewFundChannel { get; set; } = "fund_new";

    /// <summary>Yeni fon trigger adı (web'in fund_price_change trigger'ından ayrı).</summary>
    public string NewFundTrigger { get; set; } = "fund_prices_new_notify";

    /// <summary>Yeni fon trigger fonksiyon adı.</summary>
    public string NewFundFunction { get; set; } = "notify_new_fund";
}
