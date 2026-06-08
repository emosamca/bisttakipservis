using Microsoft.Extensions.Options;

namespace BistPriceService;

/// <summary>
/// Bir piyasanın anlık fiyatlarını periyodik günceller: PricesTable'daki sembolleri
/// okur, Yahoo'dan son fiyatı çeker, aynı tabloya yazar. BIST ve US için ayrı birer
/// örnek olarak çalışır.
/// </summary>
public sealed class PriceWorker : BackgroundService
{
    private readonly ILogger<PriceWorker> _logger;
    private readonly PriceRepository _repository;
    private readonly YahooFinanceClient _yahoo;
    private readonly PriceFetchOptions _global;
    private readonly MarketDefinition _market;
    private readonly MarketClock _clock;

    public PriceWorker(
        ILogger<PriceWorker> logger,
        PriceRepository repository,
        YahooFinanceClient yahoo,
        IOptions<PriceFetchOptions> global,
        MarketDefinition market)
    {
        _logger = logger;
        _repository = repository;
        _yahoo = yahoo;
        _global = global.Value;
        _market = market;
        _clock = new MarketClock(market.TimeZoneId, market.MarketOpen, market.MarketClose);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation(
            "[{Market}] anlık fiyat servisi başladı. Aralık: {Interval}sn, Sadece borsa saatleri: {Market2} ({Tz} {Open}-{Close})",
            _market.Name, _market.IntervalSeconds, _market.OnlyDuringMarketHours,
            _market.TimeZoneId, _market.MarketOpen, _market.MarketClose);

        await WaitForDatabaseAsync(stoppingToken);

        var interval = TimeSpan.FromSeconds(Math.Max(5, _market.IntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                if (_market.OnlyDuringMarketHours && !_clock.IsOpen(DateTimeOffset.UtcNow))
                {
                    _logger.LogDebug("[{Market}] borsa kapalı, tur atlanıyor.", _market.Name);
                    continue;
                }
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Market}] tarama turunda hata.", _market.Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        _logger.LogInformation("[{Market}] anlık fiyat servisi durduruluyor.", _market.Name);
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var symbols = await _repository.GetSymbolsAsync(_market.PricesTable, ct);
        if (symbols.Count == 0)
        {
            _logger.LogInformation("[{Market}] {Table} tablosunda sembol yok.", _market.Name, _market.PricesTable);
            return;
        }

        int ok = 0, fail = 0;
        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();
            var price = await _yahoo.GetPriceAsync(symbol, _market.SymbolSuffix, ct);

            if (price is > 0)
            {
                await _repository.UpdatePriceAsync(_market.PricesTable, symbol, price.Value, ct);
                ok++;
                _logger.LogInformation("[{Market}] {Symbol} = {Price}", _market.Name, symbol, price.Value);
            }
            else
            {
                fail++;
                _logger.LogWarning("[{Market}] {Symbol} için geçerli fiyat alınamadı.", _market.Name, symbol);
            }

            if (_global.PerSymbolDelayMs > 0)
                await Task.Delay(_global.PerSymbolDelayMs, ct);
        }

        _logger.LogInformation("[{Market}] tur tamamlandı. Başarılı: {Ok}, Başarısız: {Fail}, Toplam: {Total}",
            _market.Name, ok, fail, symbols.Count);
    }

    private async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _repository.EnsureConnectionAsync(ct); return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Market}] veritabanına bağlanılamadı, 15sn sonra tekrar.", _market.Name);
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
