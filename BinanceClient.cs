using System.Globalization;
using System.Text.Json;

namespace BistPriceService;

/// <summary>
/// Binance public API'sinden anlık kripto fiyatı (USDT paritesi) çeker.
///   GET /api/v3/ticker/price?symbol=&lt;SYMBOL&gt;USDT  → { "symbol": "...", "price": "..." }
/// API anahtarı gerektirmez.
/// </summary>
public sealed class BinanceClient
{
    private readonly HttpClient _http;
    private readonly ILogger<BinanceClient> _logger;

    public BinanceClient(HttpClient http, ILogger<BinanceClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>
    /// Verilen taban sembol (örn. "BTC") için &lt;symbol&gt;&lt;quote&gt; (örn. BTCUSDT)
    /// fiyatını USD cinsinden döndürür. Başarısızlıkta null.
    /// </summary>
    public async Task<decimal?> GetPriceAsync(string symbol, string quote, CancellationToken ct)
    {
        var pair = symbol.Trim().ToUpperInvariant() + quote;
        var url = $"api/v3/ticker/price?symbol={Uri.EscapeDataString(pair)}";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Binance {Pair} için HTTP {Status} döndü.", pair, (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            if (!doc.RootElement.TryGetProperty("price", out var priceEl))
            {
                _logger.LogWarning("Binance {Pair}: price alanı yok.", pair);
                return null;
            }

            var raw = priceEl.GetString();
            if (decimal.TryParse(raw, NumberStyles.Any, CultureInfo.InvariantCulture, out var price))
                return price;

            _logger.LogWarning("Binance {Pair}: price ('{Raw}') çözümlenemedi.", pair, raw);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Binance {Pair} fiyatı alınamadı.", pair);
            return null;
        }
    }
}
