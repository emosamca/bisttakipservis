using Microsoft.Extensions.Options;

namespace BistPriceService;

/// <summary>
/// Anlık USD/TRY kurunu periyodik çeker ve fx_rates tablosuna bugünün tarihiyle yazar.
/// Belirtilen pencerede (varsayılan hafta içi 10:00-23:30 TR; BIST + ABD seanslarını kapsar) çalışır.
/// </summary>
public sealed class FxWorker : BackgroundService
{
    private readonly ILogger<FxWorker> _logger;
    private readonly PriceRepository _repository;
    private readonly YahooFinanceClient _yahoo;
    private readonly FxOptions _fx;
    private readonly MarketClock _clock;
    private readonly TimeZoneInfo _tz;

    public FxWorker(
        ILogger<FxWorker> logger,
        PriceRepository repository,
        YahooFinanceClient yahoo,
        IOptions<PriceFetchOptions> global)
    {
        _logger = logger;
        _repository = repository;
        _yahoo = yahoo;
        _fx = global.Value.Fx;
        _clock = new MarketClock(_fx.TimeZoneId, _fx.MarketOpen, _fx.MarketClose);
        _tz = TimeZoneHelper.Resolve(_fx.TimeZoneId);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[FX] anlık kur servisi başladı. Çift: {Pair}, Aralık: {Interval}sn.", _fx.Pair, _fx.IntervalSeconds);

        var interval = TimeSpan.FromSeconds(Math.Max(5, _fx.IntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                if (_fx.OnlyDuringMarketHours && !_clock.IsOpen(DateTimeOffset.UtcNow))
                {
                    _logger.LogDebug("[FX] pencere dışı, tur atlanıyor.");
                    continue;
                }

                var rate = await _yahoo.GetLatestPriceAsync(_fx.Pair, stoppingToken);
                if (rate is > 0)
                {
                    var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _tz).DateTime);
                    await _repository.UpsertFxRateAsync(_fx.RatesTable, today, rate.Value, stoppingToken);
                    _logger.LogInformation("[FX] USD/TRY = {Rate} ({Date})", rate.Value, today);
                }
                else
                {
                    _logger.LogWarning("[FX] geçerli kur alınamadı.");
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FX] anlık kur turunda hata.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        _logger.LogInformation("[FX] anlık kur servisi durduruluyor.");
    }
}
