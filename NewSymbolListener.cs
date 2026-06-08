using System.Threading.Channels;
using Npgsql;

namespace BistPriceService;

/// <summary>
/// Bir piyasanın anlık fiyat tablosuna yeni sembol eklenince NOTIFY kanalını dinler
/// ve o sembolün anlık fiyatını + geçmişini -- borsa saatine bakmaksızın, hafta sonu
/// dahil -- anında çeker. BIST ve US için ayrı birer örnek olarak çalışır.
/// </summary>
public sealed class NewSymbolListener : BackgroundService
{
    private readonly ILogger<NewSymbolListener> _logger;
    private readonly NpgsqlDataSource _dataSource;
    private readonly YahooFinanceClient _yahoo;
    private readonly PriceRepository _repository;
    private readonly HistoryBackfiller _history;
    private readonly MarketDefinition _market;

    public NewSymbolListener(
        ILogger<NewSymbolListener> logger,
        NpgsqlDataSource dataSource,
        YahooFinanceClient yahoo,
        PriceRepository repository,
        HistoryBackfiller history,
        MarketDefinition market)
    {
        _logger = logger;
        _dataSource = dataSource;
        _yahoo = yahoo;
        _repository = repository;
        _history = history;
        _market = market;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await _repository.EnsureNewSymbolTriggerAsync(_market, stoppingToken); break; }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Market}] yeni-sembol trigger'ı kurulamadı, 15sn sonra tekrar.", _market.Name);
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

                await using (var cmd = new NpgsqlCommand($"LISTEN {_market.NewSymbolChannel}", conn))
                    await cmd.ExecuteNonQueryAsync(ct);

                _logger.LogInformation("[{Market}] yeni sembol dinleniyor (LISTEN {Channel}).", _market.Name, _market.NewSymbolChannel);

                while (!ct.IsCancellationRequested)
                    await conn.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[{Market}] dinleyici bağlantısı koptu, 5sn sonra yeniden.", _market.Name);
                try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private async Task ConsumeAsync(ChannelReader<string> reader, CancellationToken ct)
    {
        try
        {
            await foreach (var symbol in reader.ReadAllAsync(ct))
            {
                try
                {
                    _logger.LogInformation("[{Market}] yeni sembol algılandı: {Symbol}. Anında çekiliyor.", _market.Name, symbol);
                    var price = await _yahoo.GetPriceAsync(symbol, _market.SymbolSuffix, ct);

                    if (price is > 0)
                    {
                        await _repository.UpdatePriceAsync(_market.PricesTable, symbol, price.Value, ct);
                        _logger.LogInformation("[{Market}] yeni sembol {Symbol} = {Price} yazıldı.", _market.Name, symbol, price.Value);
                    }
                    else
                    {
                        _logger.LogWarning("[{Market}] yeni sembol {Symbol} için geçerli fiyat alınamadı.", _market.Name, symbol);
                    }

                    await _history.BackfillSymbolAsync(symbol, ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "[{Market}] yeni sembol {Symbol} işlenirken hata.", _market.Name, symbol);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
