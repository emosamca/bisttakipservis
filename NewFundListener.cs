using System.Threading.Channels;
using Microsoft.Extensions.Options;
using Npgsql;

namespace BistPriceService;

/// <summary>
/// fund_prices'a yeni fon eklenince NOTIFY kanalını (fund_new) dinler ve o fonun
/// adını + fiyatını tefas.gov.tr'nin resmi API'si üzerinden anında çekip yazar.
/// Periyodik FundWorker'dan bağımsız çalışır.
/// </summary>
public sealed class NewFundListener : BackgroundService
{
    private readonly ILogger<NewFundListener> _logger;
    private readonly NpgsqlDataSource _dataSource;
    private readonly FundClient _client;
    private readonly PriceRepository _repository;
    private readonly FundOptions _opt;

    public NewFundListener(
        ILogger<NewFundListener> logger,
        NpgsqlDataSource dataSource,
        FundClient client,
        PriceRepository repository,
        IOptions<PriceFetchOptions> global)
    {
        _logger = logger;
        _dataSource = dataSource;
        _client = client;
        _repository = repository;
        _opt = global.Value.Funds;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _repository.EnsureNewFundTriggerAsync(_opt, stoppingToken); break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[FON] yeni-fon trigger'ı kurulamadı, 15sn sonra tekrar.");
                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken); } catch (OperationCanceledException) { return; }
            }
        }

        var queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions { SingleReader = true });
        var consumer = ConsumeAsync(queue.Reader, stoppingToken);
        var listener = ListenAsync(queue.Writer, stoppingToken);
        await Task.WhenAll(consumer, listener);
    }

    private async Task ListenAsync(ChannelWriter<string> writer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var conn = await _dataSource.OpenConnectionAsync(ct);
                conn.Notification += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Payload)) writer.TryWrite(e.Payload.Trim());
                };

                await using (var cmd = new NpgsqlCommand($"LISTEN {_opt.NewFundChannel}", conn))
                    await cmd.ExecuteNonQueryAsync(ct);

                _logger.LogInformation("[FON] yeni fon dinleniyor (LISTEN {Channel}).", _opt.NewFundChannel);

                while (!ct.IsCancellationRequested)
                    await conn.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[FON] dinleyici bağlantısı koptu, 5sn sonra yeniden.");
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ConsumeAsync(ChannelReader<string> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var code in reader.ReadAllAsync(ct))
            {
                try
                {
                    _logger.LogInformation("[FON] yeni fon algılandı: {Code}. Anında çekiliyor.", code);
                    var quote = await _client.GetFundAsync(code, ct);
                    if (quote is { Price: > 0 } q)
                    {
                        await _repository.UpsertFundAsync(_opt.Table, code, q.Title, q.Price, ct);
                        _logger.LogInformation("[FON] yeni fon {Code} = {Price} ({Title}) yazıldı.", code, q.Price, q.Title);
                    }
                    else if (quote is { Price: 0 } q0)
                    {
                        await _repository.MarkFundPriceZeroAsync(_opt.Table, code, q0.Title, ct);
                        _logger.LogWarning("[FON] yeni fon {Code} henüz fiyatlanmamış (0); bir sonraki günlük taramada tekrar denenecek.", code);
                    }
                    else
                    {
                        _logger.LogWarning("[FON] yeni fon {Code} için fiyat alınamadı.", code);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[FON] yeni fon {Code} işlenirken hata.", code);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
