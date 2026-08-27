using Microsoft.Extensions.Options;

namespace BistPriceService;

/// <summary>
/// fund_prices'taki fon kodlarını (izlenen fonlar) okur, her birini tefas.gov.tr'nin
/// resmi API'si üzerinden çekip ad + fiyatı aynı tabloya yazar. Günlük tam tarama
/// DailyRunTime'da (ve istenirse başlangıçta) yapılır. Bazı fonlar bu saatte henüz
/// açıklanmamış olabilir (fiyat gelmez); bu fonlar RetryIntervalMinutes aralığıyla
/// fiyat gelene kadar tekrar denenir, ardından bir sonraki DailyRunTime'a kadar
/// beklenir. UPSERT, web'in kurduğu fund_price_change tetikleyicisini tetikler;
/// ekran SSE ile yenilenir.
/// </summary>
public sealed class FundWorker : BackgroundService
{
    private readonly ILogger<FundWorker> _logger;
    private readonly PriceRepository _repository;
    private readonly FundClient _client;
    private readonly FundOptions _opt;

    public FundWorker(
        ILogger<FundWorker> logger,
        PriceRepository repository,
        FundClient client,
        IOptions<PriceFetchOptions> global)
    {
        _logger = logger;
        _repository = repository;
        _client = client;
        _opt = global.Value.Funds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tz = TimeZoneHelper.Resolve(_opt.TimeZoneId);
        var runTime = TimeOnly.Parse(_opt.DailyRunTime);
        _logger.LogInformation("[FON] servis başladı. Tablo: {Table}, Günlük tarama: {Time} ({Tz}).",
            _opt.Table, _opt.DailyRunTime, _opt.TimeZoneId);

        await WaitForDatabaseAsync(stoppingToken);
        await EnsureSchemaAsync(stoppingToken);

        // Fiyatı henüz açıklanmamış (RunFullCycleAsync'te başarısız) fon kodları;
        // fiyat gelene ya da bir sonraki tam taramaya kadar RetryPendingAsync ile
        // tekrar denenir.
        var pending = new HashSet<string>();

        if (_opt.RunOnStartup)
        {
            try { pending = await RunFullCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogError(ex, "[FON] başlangıç turunda hata."); }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delayToRun = TimeUntilNextRun(tz, runTime);
            var retryDelay = TimeSpan.FromMinutes(Math.Max(1, _opt.RetryIntervalMinutes));
            var isFullRun = pending.Count == 0 || delayToRun <= retryDelay;
            var wait = isFullRun ? delayToRun : retryDelay;

            if (isFullRun)
                _logger.LogInformation("[FON] sonraki tam tarama {Time} sonra ({Tz} {RunTime}).",
                    wait, _opt.TimeZoneId, _opt.DailyRunTime);
            else
                _logger.LogInformation("[FON] {Count} fon için fiyat henüz açıklanmamış, {Time} sonra tekrar denenecek.",
                    pending.Count, wait);

            try { await Task.Delay(wait, stoppingToken); }
            catch (OperationCanceledException) { break; }

            if (isFullRun)
            {
                try { pending = await RunFullCycleAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "[FON] tarama turunda hata."); }
            }
            else
            {
                try { await RetryPendingAsync(pending, stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                catch (Exception ex) { _logger.LogError(ex, "[FON] tekrar deneme turunda hata."); }
            }
        }

        _logger.LogInformation("[FON] servis durduruluyor.");
    }

    /// <summary>Verilen saat diliminde bir sonraki DailyRunTime'a kalan süre.</summary>
    private static TimeSpan TimeUntilNextRun(TimeZoneInfo tz, TimeOnly runTime)
    {
        var nowLocal = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, tz).DateTime;
        var todayRun = nowLocal.Date + runTime.ToTimeSpan();
        var nextRun = nowLocal < todayRun ? todayRun : todayRun.AddDays(1);
        var delay = nextRun - nowLocal;
        return delay > TimeSpan.Zero ? delay : TimeSpan.FromMinutes(1);
    }

    /// <summary>fund_prices'taki tüm fon kodlarını tarar. Fiyatı alınamayan kodları döndürür.</summary>
    private async Task<HashSet<string>> RunFullCycleAsync(CancellationToken ct)
    {
        var codes = await _repository.GetFundCodesAsync(_opt.Table, ct);
        if (codes.Count == 0)
        {
            _logger.LogDebug("[FON] {Table} tablosunda fon yok.", _opt.Table);
            return new HashSet<string>();
        }

        var failed = new HashSet<string>();
        int ok = 0;
        foreach (var code in codes)
        {
            ct.ThrowIfCancellationRequested();
            var quote = await _client.GetFundAsync(code, ct);
            if (quote is { Price: > 0 } q)
            {
                await _repository.UpsertFundAsync(_opt.Table, code, q.Title, q.Price, ct);
                ok++;
                _logger.LogInformation("[FON] {Code} = {Price} ({Title})", code, q.Price, q.Title);
            }
            else if (quote is { Price: 0 } q0)
            {
                await _repository.MarkFundPriceZeroAsync(_opt.Table, code, q0.Title, ct);
                failed.Add(code);
                _logger.LogWarning("[FON] {Code} henüz fiyatlanmamış (0), önceki fiyat price_old'a taşındı.", code);
            }
            else
            {
                failed.Add(code);
                _logger.LogWarning("[FON] {Code} için fiyat alınamadı.", code);
            }

            if (_opt.PerFundDelayMs > 0)
                await Task.Delay(_opt.PerFundDelayMs, ct);
        }

        _logger.LogInformation("[FON] tur tamamlandı. Başarılı: {Ok}, Başarısız: {Fail}, Toplam: {Total}",
            ok, failed.Count, codes.Count);
        return failed;
    }

    /// <summary>
    /// Önceki tam taramada fiyatı gelmeyen fonları tekrar dener. Fiyatı gelenler
    /// <paramref name="pending"/>'den çıkarılır (bir sonraki tam taramaya kadar
    /// tekrar denenmezler); gelmeyenler kalır.
    /// </summary>
    private async Task RetryPendingAsync(HashSet<string> pending, CancellationToken ct)
    {
        int ok = 0;
        foreach (var code in pending.ToArray())
        {
            ct.ThrowIfCancellationRequested();
            var quote = await _client.GetFundAsync(code, ct);
            if (quote is { Price: > 0 } q)
            {
                await _repository.UpsertFundAsync(_opt.Table, code, q.Title, q.Price, ct);
                pending.Remove(code);
                ok++;
                _logger.LogInformation("[FON] {Code} = {Price} ({Title}) (gecikmeli açıklandı)", code, q.Price, q.Title);
            }
            else if (quote is { Price: 0 } q0)
            {
                await _repository.MarkFundPriceZeroAsync(_opt.Table, code, q0.Title, ct);
                // pending'de kalır; hâlâ açıklanmadı.
            }

            if (_opt.PerFundDelayMs > 0)
                await Task.Delay(_opt.PerFundDelayMs, ct);
        }

        _logger.LogInformation("[FON] tekrar deneme turu tamamlandı. Gelen: {Ok}, Hâlâ bekleyen: {Pending}",
            ok, pending.Count);
    }

    private async Task WaitForDatabaseAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _repository.EnsureConnectionAsync(ct); return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FON] veritabanına bağlanılamadı, 15sn sonra tekrar.");
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>price_old kolonunun var olduğundan emin olur (idempotent, ilk açılışta bir kez).</summary>
    private async Task EnsureSchemaAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _repository.EnsureFundPriceOldColumnAsync(_opt.Table, ct); return; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FON] price_old kolonu eklenemedi, 15sn sonra tekrar.");
                try { await Task.Delay(TimeSpan.FromSeconds(15), ct); } catch (OperationCanceledException) { return; }
            }
        }
    }
}
