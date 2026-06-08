namespace BistPriceService;

/// <summary>
/// Kıymetli maden (altın/gümüş) gram-TL ayarları. Geçmiş tutulmaz; sadece son değer
/// metal_prices tablosuna yazılır.
///   gram_tl = (ons_usd × USDTRY) / 31.1035   (altın ons GC=F, gümüş ons SI=F)
/// </summary>
public sealed class MetalOptions
{
    /// <summary>Anlık fiyat tablosu (PK metal; price, updated_at).</summary>
    public string Table { get; set; } = "metal_prices";

    /// <summary>Yahoo altın ons sembolü.</summary>
    public string GoldSymbol { get; set; } = "GC=F";

    /// <summary>Yahoo gümüş ons sembolü.</summary>
    public string SilverSymbol { get; set; } = "SI=F";

    /// <summary>USD/TRY kuru sembolü.</summary>
    public string FxPair { get; set; } = "USDTRY=X";

    /// <summary>Ons → gram dönüşüm böleni (troy ons).</summary>
    public decimal GramDivisor { get; set; } = 31.1035m;

    /// <summary>Turlar arası bekleme (saniye).</summary>
    public int IntervalSeconds { get; set; } = 300;

    /// <summary>Sadece belirtilen pencerede mi çekilsin?</summary>
    public bool OnlyDuringMarketHours { get; set; } = true;

    /// <summary>Pencere saat dilimi.</summary>
    public string TimeZoneId { get; set; } = "Turkey Standard Time";

    /// <summary>Çekme penceresi "HH:mm" (hafta içi 24 saat için 00:00-23:59).</summary>
    public string MarketOpen { get; set; } = "00:00";
    public string MarketClose { get; set; } = "23:59";
}
