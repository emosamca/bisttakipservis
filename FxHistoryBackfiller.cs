using System.Globalization;
using Microsoft.Extensions.Options;

namespace BistPriceService;

/// <summary>
/// USD/TRY günlük kapanışlarını fx_rates_history tablosuna doldurur/tamamlar.
/// Başlangıçta bir kez, sonra HistoryRefreshHours'ta bir tekrar (varsayılan günde 1).
/// </summary>
public sealed class FxHistoryBackfiller : BackgroundService
{
    private readonly ILogger<FxHistoryBackfiller> _logger;
    private readonly PriceRepository _repository;
    private readonly YahooFinanceClient _yahoo;
    private readonly FxOptions _fx;
    private readonly DateOnly _startDate;
    private readonly TimeZoneInfo _tz;

    public FxHistoryBackfiller(
        ILogger<FxHistoryBackfiller> logger,
        PriceRepository repository,
        YahooFinanceClient yahoo,
        IOptions<PriceFetchOptions> global)
    {
        _logger = logger;
        _repository = repository;
        _yahoo = yahoo;
        _fx = global.Value.Fx;
        _startDate = DateOnly.ParseExact(_fx.HistoryStartDate, "yyyy-MM-dd", CultureInfo.InvariantCulture);
        _tz = TimeZoneHelper.Resolve(_fx.TimeZoneId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _repository.EnsureConnectionAsync(stoppingToken);
                await _repository.EnsureFxHistoryTableAsync(_fx.HistoryTable, stoppingToken);
                break;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FX] geçmiş tablosu hazırlanamadı, 15sn sonra tekrar.");
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }

        do
        {
            try
            {
                var bars = await _yahoo.GetDailyHistoryAsync(_fx.Pair, "", _startDate, _tz, stoppingToken);
                if (bars.Count > 0)
                {
                    var written = await _repository.UpsertFxHistoryAsync(_fx.HistoryTable, bars, stoppingToken);
                    _logger.LogInformation("[FX] geçmiş: {Bars} gün çekildi, {Written} satır yazıldı ({Start}'ten itibaren).",
                        bars.Count, written, _startDate);
                }
                else
                {
                    _logger.LogWarning("[FX] geçmiş veri alınamadı.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "[FX] geçmiş tarama turunda hata."); }

            if (_fx.HistoryRefreshHours <= 0) break;
            try { await Task.Delay(TimeSpan.FromHours(_fx.HistoryRefreshHours), stoppingToken); }
            catch (OperationCanceledException) { break; }
        }
        while (!stoppingToken.IsCancellationRequested);

        _logger.LogInformation("[FX] geçmiş doldurucu durduruldu.");
    }
}
