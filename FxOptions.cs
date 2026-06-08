namespace BistPriceService;

/// <summary>
/// USD/TRY döviz kuru ayarları. Anlık kur fx_rates'e (date=bugün) yazılır;
/// günlük kapanışlar fx_rates_history'ye doldurulur.
/// </summary>
public sealed class FxOptions
{
    /// <summary>Yahoo döviz sembolü (USD/TRY için "USDTRY=X").</summary>
    public string Pair { get; set; } = "USDTRY=X";

    /// <summary>Anlık kur tablosu (PK date; rate, updated_at).</summary>
    public string RatesTable { get; set; } = "fx_rates";

    /// <summary>Günlük kapanış tablosu (PK date; rate, updated_at).</summary>
    public string HistoryTable { get; set; } = "fx_rates_history";

    /// <summary>Geçmiş kapanışların başlangıç tarihi "yyyy-MM-dd".</summary>
    public string HistoryStartDate { get; set; } = "2025-08-01";

    /// <summary>Geçmiş taramasının tekrar sıklığı (saat).</summary>
    public int HistoryRefreshHours { get; set; } = 24;

    /// <summary>Anlık kur turları arası bekleme (saniye).</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Sadece belirtilen saat aralığında mı çekilsin?</summary>
    public bool OnlyDuringMarketHours { get; set; } = true;

    /// <summary>Anlık kur çekme penceresi için saat dilimi.</summary>
    public string TimeZoneId { get; set; } = "Turkey Standard Time";

    /// <summary>Anlık kur çekme penceresi "HH:mm" (BIST + ABD seanslarını kapsar).</summary>
    public string MarketOpen { get; set; } = "10:00";
    public string MarketClose { get; set; } = "23:30";
}
