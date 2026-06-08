namespace BistPriceService;

/// <summary>
/// Kripto fiyat ayarları. crypto_prices'taki semboller (yalnız sahip olunan coin'ler)
/// okunur, her biri Binance'den &lt;symbol&gt;USDT paritesiyle çekilip aynı tabloya
/// yazılır. Kripto 7/24 işlem gördüğü için borsa saati kısıtı yoktur.
/// </summary>
public sealed class CryptoOptions
{
    /// <summary>Anlık fiyat tablosu (PK symbol; price, updated_at).</summary>
    public string Table { get; set; } = "crypto_prices";

    /// <summary>Parite kotasyonu (sembole eklenen son ek).</summary>
    public string QuoteSuffix { get; set; } = "USDT";

    /// <summary>Turlar arası bekleme (saniye). Kripto 7/24, varsayılan 60sn.</summary>
    public int IntervalSeconds { get; set; } = 60;

    /// <summary>Semboller arası bekleme (ms).</summary>
    public int PerSymbolDelayMs { get; set; } = 100;
}
