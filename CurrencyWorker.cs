using Microsoft.Extensions.Options;

namespace BistPriceService;

/// <summary>
/// Döviz fiyatlarını (EUR vb.) periyodik çekip currency_prices'a yazar. Yahoo "EURTRY=X"
/// doğrudan EUR/TRY (₺) verdiği için dönüşüm yoktur. Geçmiş tutulmaz; sadece son değer
/// (UPSERT). Pencere varsayılan hafta içi 24 saat.
/// </summary>
public sealed class CurrencyWorker : BackgroundService
{
    private readonly ILogger<CurrencyWorker> _logger;
    private readonly PriceRepository _repository;
    private readonly YahooFinanceClient _yahoo;
    private readonly CurrencyOptions _opt;
    private readonly MarketClock _clock;

    public CurrencyWorker(
        ILogger<CurrencyWorker> logger,
        PriceRepository repository,
        YahooFinanceClient yahoo,
        IOptions<PriceFetchOptions> global)
    {
        _logger = logger;
        _repository = repository;
        _yahoo = yahoo;
        _opt = global.Value.Currencies;
        _clock = new MarketClock(_opt.TimeZoneId, _opt.MarketOpen, _opt.MarketClose);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (_opt.Items.Count == 0)
        {
            _logger.LogInformation("[CUR] yapılandırılmış döviz yok, servis boşta.");
            return;
        }

        _logger.LogInformation("[CUR] döviz servisi başladı. Dövizler: {List}, Aralık: {Interval}sn.",
            string.Join(", ", _opt.Items.Select(i => $"{i.Code}={i.Pair}")), _opt.IntervalSeconds);

        var interval = TimeSpan.FromSeconds(Math.Max(5, _opt.IntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                if (_opt.OnlyDuringMarketHours && !_clock.IsOpen(DateTimeOffset.UtcNow))
                {
                    _logger.LogDebug("[CUR] pencere dışı, tur atlanıyor.");
                    continue;
                }

                foreach (var item in _opt.Items)
                {
                    stoppingToken.ThrowIfCancellationRequested();
                    var price = await _yahoo.GetLatestPriceAsync(item.Pair, stoppingToken);
                    if (price is > 0)
                    {
                        await _repository.UpsertCurrencyAsync(_opt.Table, item.Code, price.Value, stoppingToken);
                        _logger.LogInformation("[CUR] {Code} = {Price} ₺ ({Pair})", item.Code, price.Value, item.Pair);
                    }
                    else
                    {
                        _logger.LogWarning("[CUR] {Code} ({Pair}) için geçerli değer alınamadı, atlandı.", item.Code, item.Pair);
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CUR] turda hata.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        _logger.LogInformation("[CUR] döviz servisi durduruluyor.");
    }
}
