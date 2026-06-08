using System.Globalization;
using Microsoft.Extensions.Options;

namespace BistPriceService;

/// <summary>
/// Bir piyasanın geçmiş günlük kapanışlarını (close + adj_close) doldurur/tamamlar.
/// Başlangıçta bir kez, sonra HistoryRefreshHours'ta bir tekrar tarar. Yeni eklenen
/// semboller için <see cref="BackfillSymbolAsync"/> NewSymbolListener'dan da çağrılır.
/// </summary>
public sealed class HistoryBackfiller : BackgroundService
{
    private readonly ILogger<HistoryBackfiller> _logger;
    private readonly PriceRepository _repository;
    private readonly YahooFinanceClient _yahoo;
    private readonly PriceFetchOptions _global;
    private readonly MarketDefinition _market;
    private readonly DateOnly _startDate;
    private readonly TimeZoneInfo _exchangeTz;

    public HistoryBackfiller(
        ILogger<HistoryBackfiller> logger,
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
        _startDate = DateOnly.ParseExact(market.HistoryStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        _exchangeTz = TimeZoneHelper.Resolve(market.TimeZoneId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _repository.EnsureConnectionAsync(stoppingToken);
                await _repository.EnsureHistoryTableAsync(_market.HistoryTable, stoppingToken);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Market}] geçmiş tablosu hazırlanamadı, 15sn sonra tekrar.", _market.Name);
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }

        do
        {
            try { await BackfillAllAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "[{Market}] geçmiş tarama turunda hata.", _market.Name); }

            if (_market.HistoryRefreshHours <= 0) break;
            try { await Task.Delay(TimeSpan.FromHours(_market.HistoryRefreshHours), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        while (!stoppingToken.IsCancellationRequested);

        _logger.LogInformation("[{Market}] geçmiş doldurucu durduruldu.", _market.Name);
    }

    private async Task BackfillAllAsync(CancellationToken ct)
    {
        var symbols = await _repository.GetSymbolsAsync(_market.PricesTable, ct);
        if (symbols.Count == 0)
        {
            _logger.LogInformation("[{Market}] geçmiş için sembol yok.", _market.Name);
            return;
        }

        _logger.LogInformation("[{Market}] geçmiş taraması başladı ({Count} sembol, {Start}'ten itibaren).",
            _market.Name, symbols.Count, _startDate);

        int total = 0;
        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();
            total += await BackfillSymbolAsync(symbol, ct);
            if (_global.PerSymbolDelayMs > 0) await Task.Delay(_global.PerSymbolDelayMs, ct);
        }

        _logger.LogInformation("[{Market}] geçmiş taraması tamamlandı. Toplam yazılan satır: {Total}", _market.Name, total);
    }

    /// <summary>Tek bir sembolün geçmişini çekip yazar. Yazılan satır sayısını döndürür.</summary>
    public async Task<int> BackfillSymbolAsync(string symbol, CancellationToken ct)
    {
        var bars = await _yahoo.GetDailyHistoryAsync(symbol, _market.SymbolSuffix, _startDate, _exchangeTz, ct);
        if (bars.Count == 0)
        {
            _logger.LogWarning("[{Market}] {Symbol} için geçmiş veri alınamadı.", _market.Name, symbol);
            return 0;
        }

        var written = await _repository.UpsertHistoryAsync(_market.HistoryTable, symbol, bars, ct);
        _logger.LogInformation("[{Market}] {Symbol} geçmişi: {Bars} gün çekildi, {Written} satır yazıldı.",
            _market.Name, symbol, bars.Count, written);
        return written;
    }
}
