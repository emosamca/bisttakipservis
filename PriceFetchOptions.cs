namespace BistPriceService;

/// <summary>
/// appsettings.json içindeki "PriceFetch" bölümünün karşılığı. Piyasalar arası ortak
/// (global) ayarlar burada; piyasaya özgü ayarlar <see cref="MarketDefinition"/> içinde.
/// </summary>
public sealed class PriceFetchOptions
{
    public const string SectionName = "PriceFetch";

    /// <summary>Tek bir HTTP isteği için zaman aşımı (saniye).</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>Semboller arası bekleme (ms) — rate-limit'e takılmamak için.</summary>
    public int PerSymbolDelayMs { get; set; } = 250;

    /// <summary>Takip edilen piyasalar (BIST, US, ...).</summary>
    public List<MarketDefinition> Markets { get; set; } = new();

    /// <summary>USD/TRY döviz kuru ayarları.</summary>
    public FxOptions Fx { get; set; } = new();

    /// <summary>Kıymetli maden (altın/gümüş) gram-TL ayarları.</summary>
    public MetalOptions Metals { get; set; } = new();

    /// <summary>Döviz (EUR vb.) anlık fiyat ayarları.</summary>
    public CurrencyOptions Currencies { get; set; } = new();

    /// <summary>Kripto (Binance) anlık fiyat ayarları.</summary>
    public CryptoOptions Crypto { get; set; } = new();

    /// <summary>TEFAS fon fiyat ayarları.</summary>
    public FundOptions Funds { get; set; } = new();
}
