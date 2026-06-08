using Microsoft.Extensions.Options;

namespace BistPriceService;

/// <summary>
/// Altın ve gümüşün gram-TL fiyatını periyodik hesaplayıp metal_prices'a yazar.
///   gram_tl = (ons_usd × USDTRY) / 31.1035
/// Ons fiyatları GC=F (altın) ve SI=F (gümüş), kur USDTRY=X üzerinden Yahoo'dan çekilir.
/// Geçmiş tutulmaz; sadece son değer (UPSERT). Pencere varsayılan hafta içi 24 saat.
/// </summary>
public sealed class MetalWorker : BackgroundService
{
    private readonly ILogger<MetalWorker> _logger;
    private readonly PriceRepository _repository;
    private readonly YahooFinanceClient _yahoo;
    private readonly MetalOptions _opt;
    private readonly MarketClock _clock;

    public MetalWorker(
        ILogger<MetalWorker> logger,
        PriceRepository repository,
        YahooFinanceClient yahoo,
        IOptions<PriceFetchOptions> global)
    {
        _logger = logger;
        _repository = repository;
        _yahoo = yahoo;
        _opt = global.Value.Metals;
        _clock = new MarketClock(_opt.TimeZoneId, _opt.MarketOpen, _opt.MarketClose);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[METAL] gram-TL servisi başladı. Altın: {Gold}, Gümüş: {Silver}, Kur: {Fx}, Aralık: {Interval}sn.",
            _opt.GoldSymbol, _opt.SilverSymbol, _opt.FxPair, _opt.IntervalSeconds);

        var interval = TimeSpan.FromSeconds(Math.Max(5, _opt.IntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try
            {
                if (_opt.OnlyDuringMarketHours && !_clock.IsOpen(DateTimeOffset.UtcNow))
                {
                    _logger.LogDebug("[METAL] pencere dışı, tur atlanıyor.");
                    continue;
                }
                await RunCycleAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[METAL] turda hata.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        _logger.LogInformation("[METAL] gram-TL servisi durduruluyor.");
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var usdtry = await _yahoo.GetLatestPriceAsync(_opt.FxPair, ct);
        if (usdtry is not > 0)
        {
            _logger.LogWarning("[METAL] USD/TRY alınamadı, tur atlanıyor.");
            return;
        }

        await WriteMetalAsync("gold", _opt.GoldSymbol, usdtry.Value, ct);
        await WriteMetalAsync("silver", _opt.SilverSymbol, usdtry.Value, ct);
    }

    private async Task WriteMetalAsync(string metal, string onsSymbol, decimal usdtry, CancellationToken ct)
    {
        var ons = await _yahoo.GetLatestPriceAsync(onsSymbol, ct);
        if (ons is not > 0)
        {
            _logger.LogWarning("[METAL] {Metal} ons fiyatı ({Symbol}) alınamadı, atlandı.", metal, onsSymbol);
            return;
        }

        var gramTl = ons.Value * usdtry / _opt.GramDivisor;
        await _repository.UpsertMetalAsync(_opt.Table, metal, gramTl, ct);
        _logger.LogInformation("[METAL] {Metal} = {Gram} TL/gram (ons {Ons} × kur {Fx})",
            metal, Math.Round(gramTl, 4), ons.Value, usdtry);
    }
}
