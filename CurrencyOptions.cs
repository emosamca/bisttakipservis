namespace BistPriceService;

/// <summary>
/// currency_prices tablosuna yazılacak döviz fiyatları. Yahoo "EURTRY=X" doğrudan
/// EUR/TRY (₺) verir, dönüşüm gerekmez. Geçmiş tutulmaz; sadece son değer (UPSERT).
/// </summary>
public sealed class CurrencyOptions
{
    /// <summary>Anlık fiyat tablosu (PK currency; price, updated_at).</summary>
    public string Table { get; set; } = "currency_prices";

    /// <summary>Takip edilen dövizler (code = tablodaki currency, pair = Yahoo sembolü).</summary>
    public List<CurrencyItem> Items { get; set; } = new();

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

/// <summary>Tek bir döviz: tablodaki kod ve Yahoo sembolü.</summary>
public sealed class CurrencyItem
{
    public string Code { get; set; } = "";   // örn. "eur"
    public string Pair { get; set; } = "";    // örn. "EURTRY=X"
}
