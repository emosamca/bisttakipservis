using System.Text.Json;

namespace BistPriceService;

/// <summary>
/// Yahoo Finance "chart" uç noktasından anlık ve geçmiş fiyat çeker.
/// Sembol son eki ve saat dilimi piyasaya göre çağrı başına verilir
/// (BIST=".IS", US="" , FX sembolü "USDTRY=X"). API anahtarı gerektirmez.
/// </summary>
public sealed class YahooFinanceClient
{
    private readonly HttpClient _http;
    private readonly ILogger<YahooFinanceClient> _logger;

    public YahooFinanceClient(HttpClient http, ILogger<YahooFinanceClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    /// <summary>Ham sembolü Yahoo formatına çevirir (zaten son ek varsa dokunmaz).</summary>
    public static string ToYahooSymbol(string symbol, string suffix)
    {
        var s = symbol.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(suffix)) return s;
        return s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ? s : s + suffix;
    }

    /// <summary>Sembol (son ekiyle) için son fiyatı döndürür. Başarısızlıkta null.</summary>
    public Task<decimal?> GetPriceAsync(string symbol, string suffix, CancellationToken ct)
        => GetLatestPriceAsync(ToYahooSymbol(symbol, suffix), ct);

    /// <summary>Verilen Yahoo sembolü için son fiyatı döndürür. Başarısızlıkta null.</summary>
    public async Task<decimal?> GetLatestPriceAsync(string yahooSymbol, CancellationToken ct)
    {
        var url = $"v8/finance/chart/{Uri.EscapeDataString(yahooSymbol)}?interval=1d&range=1d";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Yahoo {Symbol} için HTTP {Status} döndü.", yahooSymbol, (int)resp.StatusCode);
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var chart = doc.RootElement.GetProperty("chart");

            if (chart.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            {
                _logger.LogWarning("Yahoo {Symbol} hata: {Error}", yahooSymbol, err.ToString());
                return null;
            }

            if (!chart.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            {
                _logger.LogWarning("Yahoo {Symbol} için sonuç bulunamadı.", yahooSymbol);
                return null;
            }

            var meta = result[0].GetProperty("meta");

            if (meta.TryGetProperty("regularMarketPrice", out var rmp) && rmp.ValueKind == JsonValueKind.Number)
                return rmp.GetDecimal();

            if (meta.TryGetProperty("chartPreviousClose", out var prev) && prev.ValueKind == JsonValueKind.Number)
            {
                _logger.LogDebug("Yahoo {Symbol}: regularMarketPrice yok, previousClose kullanıldı.", yahooSymbol);
                return prev.GetDecimal();
            }

            _logger.LogWarning("Yahoo {Symbol}: fiyat alanı bulunamadı.", yahooSymbol);
            return null;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Yahoo {Symbol} fiyatı alınamadı.", yahooSymbol);
            return null;
        }
    }

    /// <summary>
    /// Sembol için [from, bugün] aralığındaki GÜNLÜK kapanışları (close ve adjclose) çeker.
    /// İşlem günü tarihi <paramref name="exchangeTz"/> ile belirlenir. Borsa kapalı günler
    /// Yahoo'da zaten yoktur. Başarısızlıkta boş liste döner.
    /// </summary>
    public async Task<IReadOnlyList<DailyBar>> GetDailyHistoryAsync(
        string symbol, string suffix, DateOnly from, TimeZoneInfo exchangeTz, CancellationToken ct)
    {
        var yahooSymbol = ToYahooSymbol(symbol, suffix);
        var period1 = new DateTimeOffset(from.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).ToUnixTimeSeconds();
        var period2 = DateTimeOffset.UtcNow.AddDays(1).ToUnixTimeSeconds();
        var url = $"v8/finance/chart/{Uri.EscapeDataString(yahooSymbol)}?period1={period1}&period2={period2}&interval=1d";

        try
        {
            using var resp = await _http.GetAsync(url, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Yahoo geçmiş {Symbol} için HTTP {Status} döndü.", yahooSymbol, (int)resp.StatusCode);
                return Array.Empty<DailyBar>();
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

            var chart = doc.RootElement.GetProperty("chart");
            if (chart.TryGetProperty("error", out var err) && err.ValueKind != JsonValueKind.Null)
            {
                _logger.LogWarning("Yahoo geçmiş {Symbol} hata: {Error}", yahooSymbol, err.ToString());
                return Array.Empty<DailyBar>();
            }

            if (!chart.TryGetProperty("result", out var result) ||
                result.ValueKind != JsonValueKind.Array || result.GetArrayLength() == 0)
            {
                return Array.Empty<DailyBar>();
            }

            var res0 = result[0];
            if (!res0.TryGetProperty("timestamp", out var timestamps) || timestamps.ValueKind != JsonValueKind.Array)
            {
                _logger.LogInformation("Yahoo geçmiş {Symbol}: zaman damgası yok (veri olmayabilir).", yahooSymbol);
                return Array.Empty<DailyBar>();
            }

            var indicators = res0.GetProperty("indicators");
            var quote = indicators.GetProperty("quote")[0];
            JsonElement closes = quote.TryGetProperty("close", out var c) && c.ValueKind == JsonValueKind.Array ? c : default;

            JsonElement adj = default;
            if (indicators.TryGetProperty("adjclose", out var adjArr) &&
                adjArr.ValueKind == JsonValueKind.Array && adjArr.GetArrayLength() > 0)
            {
                adjArr[0].TryGetProperty("adjclose", out adj);
            }

            int n = timestamps.GetArrayLength();
            var bars = new List<DailyBar>(n);
            for (int i = 0; i < n; i++)
            {
                var ts = timestamps[i].GetInt64();
                var local = TimeZoneInfo.ConvertTime(DateTimeOffset.FromUnixTimeSeconds(ts), exchangeTz);
                var date = DateOnly.FromDateTime(local.DateTime);

                decimal? close = ReadDecimal(closes, i);
                decimal? adjClose = ReadDecimal(adj, i);

                if (close is null && adjClose is null) continue;

                bars.Add(new DailyBar(date, close, adjClose));
            }
            return bars;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Yahoo geçmiş {Symbol} alınamadı.", yahooSymbol);
            return Array.Empty<DailyBar>();
        }
    }

    private static decimal? ReadDecimal(JsonElement array, int index)
    {
        if (array.ValueKind != JsonValueKind.Array || index >= array.GetArrayLength())
            return null;
        var el = array[index];
        return el.ValueKind == JsonValueKind.Number ? el.GetDecimal() : null;
    }
}

/// <summary>Tek bir işlem gününün kapanış verisi.</summary>
public readonly record struct DailyBar(DateOnly Date, decimal? Close, decimal? AdjClose);
