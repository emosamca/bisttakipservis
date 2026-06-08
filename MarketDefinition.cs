namespace BistPriceService;

/// <summary>
/// Bir hisse senedi piyasasının (BIST, US, ...) tüm ayarları. Servisler bu tanıma
/// göre generic çalışır; yeni bir piyasa eklemek için appsettings'e bir kayıt yeterli.
/// </summary>
public sealed class MarketDefinition
{
    /// <summary>Loglarda görünen ad (örn. "BIST", "US").</summary>
    public string Name { get; set; } = "";

    /// <summary>Anlık fiyat tablosu (symbol UNIQUE, price, updated_at).</summary>
    public string PricesTable { get; set; } = "";

    /// <summary>Günlük kapanış tablosu (PK symbol,date; close, adj_close, updated_at).</summary>
    public string HistoryTable { get; set; } = "";

    /// <summary>Yeni sembol NOTIFY kanalı.</summary>
    public string NewSymbolChannel { get; set; } = "";

    /// <summary>Yeni sembol trigger adı.</summary>
    public string NewSymbolTrigger { get; set; } = "";

    /// <summary>Yeni sembol trigger fonksiyon adı.</summary>
    public string NewSymbolFunction { get; set; } = "";

    /// <summary>Yahoo sembol son eki (BIST=".IS", US="").</summary>
    public string SymbolSuffix { get; set; } = "";

    /// <summary>Borsa saatlerinin değerlendirileceği saat dilimi (Windows id).</summary>
    public string TimeZoneId { get; set; } = "Turkey Standard Time";

    /// <summary>Borsa açılış/kapanış "HH:mm" (yerel borsa saatiyle).</summary>
    public string MarketOpen { get; set; } = "10:00";
    public string MarketClose { get; set; } = "18:10";

    /// <summary>Anlık fiyat turları arası bekleme (saniye).</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Sadece borsa açık saatlerde mi çekilsin?</summary>
    public bool OnlyDuringMarketHours { get; set; } = true;

    /// <summary>Geçmiş kapanışların başlangıç tarihi "yyyy-MM-dd".</summary>
    public string HistoryStartDate { get; set; } = "2025-08-01";

    /// <summary>Geçmiş taramasının tekrar sıklığı (saat). 0 => sadece başlangıçta.</summary>
    public int HistoryRefreshHours { get; set; } = 24;
}
