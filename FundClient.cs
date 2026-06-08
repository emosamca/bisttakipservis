using System.Globalization;
using System.Text.RegularExpressions;

namespace BistPriceService;

/// <summary>
/// TEFAS fon fiyatlarını hangikredi.com üzerinden (TEFAS verisini yansıtır) çeker.
/// TEFAS'ın kendi sitesi Imperva bot koruması altında olduğu ve sunucu IP'si
/// engellendiği için doğrudan TEFAS yerine bu Imperva'sız kaynak kullanılır.
///   GET https://www.hangikredi.com/yatirim-araclari/fon/&lt;KOD&gt;
/// Sayfadan fon adı (title) ve birim pay fiyatı (initial-data-last) ayrıştırılır.
/// </summary>
public sealed partial class FundClient
{
    private readonly HttpClient _http;
    private readonly ILogger<FundClient> _logger;

    public FundClient(HttpClient http, ILogger<FundClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    [GeneratedRegex("initial-data-last\"\\s*>\\s*([0-9.,]+)", RegexOptions.IgnoreCase)]
    private static partial Regex PriceRegex();

    [GeneratedRegex("<title>\\s*([A-Z0-9]+)\\s+Fon\\s*-\\s*(.+?)\\s*</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex TitleRegex();

    public readonly record struct FundQuote(string Title, decimal Price);

    /// <summary>Fon kodu için (ad, fiyat) döndürür. Başarısızlıkta null.</summary>
    public async Task<FundQuote?> GetFundAsync(string code, CancellationToken ct)
    {
        var url = $"yatirim-araclari/fon/{Uri.EscapeDataString(code)}";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Fon {Code} için HTTP {Status} döndü.", code, (int)resp.StatusCode);
                return null;
            }

            var html = await resp.Content.ReadAsStringAsync(ct);

            var pm = PriceRegex().Match(html);
            if (!pm.Success || !TryParseTrNumber(pm.Groups[1].Value, out var price) || price <= 0)
            {
                _logger.LogWarning("Fon {Code}: fiyat ayrıştırılamadı.", code);
                return null;
            }

            // Ad: sayfa başlığından "KOD Fon - AD". Bulunamazsa kodu kullan.
            var tm = TitleRegex().Match(html);
            var title = tm.Success ? tm.Groups[2].Value.Trim() : code;

            return new FundQuote(title, price);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fon {Code} fiyatı alınamadı.", code);
            return null;
        }
    }

    /// <summary>Türkçe sayı ("1.234,5678") → decimal. Binlik '.', ondalık ','.</summary>
    private static bool TryParseTrNumber(string raw, out decimal value)
    {
        var s = raw.Trim().Replace(".", "").Replace(",", ".");
        return decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out value);
    }
}
