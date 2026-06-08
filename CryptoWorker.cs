using Microsoft.Extensions.Options;

namespace BistPriceService;

/// <summary>
/// crypto_prices'taki sembolleri (yalnız sahip olunan coin'ler) okur, her birini
/// Binance'den &lt;symbol&gt;USDT paritesiyle çekip aynı tabloya yazar. Kripto 7/24
/// işlem gördüğü için borsa saati/hafta sonu kısıtı yoktur; sürekli çalışır.
/// </summary>
public sealed class CryptoWorker : BackgroundService
{
    private readonly ILogger<CryptoWorker> _logger;
    private readonly PriceRepository _repository;
    private readonly BinanceClient _binance;
    private readonly CryptoOptions _opt;

    public CryptoWorker(
        ILogger<CryptoWorker> logger,
        PriceRepository repository,
        BinanceClient binance,
        IOptions<PriceFetchOptions> global)
    {
        _logger = logger;
        _repository = repository;
        _binance = binance;
        _opt = global.Value.Crypto;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("[CRYPTO] servis başladı (7/24). Kotasyon: {Quote}, Aralık: {Interval}sn.",
            _opt.QuoteSuffix, _opt.IntervalSeconds);

        await WaitForDatabaseAsync(stoppingToken);

        var interval = TimeSpan.FromSeconds(Math.Max(5, _opt.IntervalSeconds));
        using var timer = new PeriodicTimer(interval);

        do
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "[CRYPTO] turda hata."); }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));

        _logger.LogInformation("[CRYPTO] servis durduruluyor.");
    }

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var symbols = await _repository.GetSymbolsAsync(_opt.Table, ct);
        if (symbols.Count == 0)
        {
            _logger.LogDebug("[CRYPTO] {Table} tablosunda sembol yok.", _opt.Table);
            return;
        }

        int ok = 0, fail = 0;
        foreach (var symbol in symbols)
        {
            ct.ThrowIfCancellationRequested();
            var price = await _binance.GetPriceAsync(symbol, _opt.QuoteSuffix, ct);

            if (price is > 0)
            {
                await _repository.UpdatePriceAsync(_opt.Table, symbol, price.Value, ct);
                ok++;
                _logger.LogInformation("[CRYPTO] {Symbol} = {Price} USD", symbol, price.Value);
            }
            else
            {
                fail++;
                _logger.LogWarning("[CRYPTO] {Symbol} için geçerli fiyat alınamadı.", symbol);
            }

            if (_opt.PerSymbolDelayMs > 0)
                await Task.Delay(_opt.PerSymbolDelayMs, ct);
        }

        _logger.LogInformation("[CRYPTO] tur tamamlandı. Başarılı: {Ok}, Başarısız: {Fail}, Toplam: {Total}",
            ok, fail, symbols.Count);
    }

    private async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _repository.EnsureConnectionAsync(ct); return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[CRYPTO] veritabanına bağlanılamadı, 15sn sonra tekrar.");
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
