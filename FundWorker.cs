using Microsoft.Extensions.Options;

namespace BistPriceService;

/// <summary>
/// fund_prices'taki fon kodlarını (izlenen fonlar) okur, her birini hangikredi.com
/// üzerinden çekip ad + fiyatı aynı tabloya yazar. TEFAS fiyatları günde bir
/// açıklandığı için RefreshHours'ta bir (varsayılan 12s) taranır. UPSERT, web'in
/// kurduğu fund_price_change tetikleyicisini tetikler; ekran SSE ile yenilenir.
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

        if (_opt.RunOnStartup)
        {
            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex) { _logger.LogError(ex, "[FON] başlangıç turunda hata."); }
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = TimeUntilNextRun(tz, runTime);
            _logger.LogInformation("[FON] sonraki tam tarama {Time} sonra ({Tz} {RunTime}).",
                delay, _opt.TimeZoneId, _opt.DailyRunTime);
            try { await Task.Delay(delay, stoppingToken); }
            catch (OperationCanceledException) { break; }

            try { await RunCycleAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex) { _logger.LogError(ex, "[FON] tarama turunda hata."); }
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

    private async Task RunCycleAsync(CancellationToken ct)
    {
        var codes = await _repository.GetFundCodesAsync(_opt.Table, ct);
        if (codes.Count == 0)
        {
            _logger.LogDebug("[FON] {Table} tablosunda fon yok.", _opt.Table);
            return;
        }

        int ok = 0, fail = 0;
        foreach (var code in codes)
        {
            ct.ThrowIfCancellationRequested();
            var quote = await _client.GetFundAsync(code, ct);
            if (quote is { } q)
            {
                await _repository.UpsertFundAsync(_opt.Table, code, q.Title, q.Price, ct);
                ok++;
                _logger.LogInformation("[FON] {Code} = {Price} ({Title})", code, q.Price, q.Title);
            }
            else
            {
                fail++;
                _logger.LogWarning("[FON] {Code} için fiyat alınamadı.", code);
            }

            if (_opt.PerFundDelayMs > 0)
                await Task.Delay(_opt.PerFundDelayMs, ct);
        }

        _logger.LogInformation("[FON] tur tamamlandı. Başarılı: {Ok}, Başarısız: {Fail}, Toplam: {Total}",
            ok, fail, codes.Count);
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
}
