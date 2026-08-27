using Npgsql;
using NpgsqlTypes;

namespace BistPriceService;

/// <summary>
/// prices / us_prices ve price_history / us_price_history tabloları ile fx_rates /
/// fx_rates_history üzerinde okuma-yazma. Tablo adları güvenilir yapılandırmadan
/// (MarketDefinition / FxOptions) gelir, kullanıcı girdisi değildir; bu yüzden SQL'e
/// doğrudan gömülmeleri güvenlidir.
/// </summary>
public sealed class PriceRepository
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<PriceRepository> _logger;

    public PriceRepository(NpgsqlDataSource dataSource, ILogger<PriceRepository> logger)
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    /// <summary>Bağlantıyı test eder; başarısızsa exception fırlatır.</summary>
    public async Task EnsureConnectionAsync(CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand("SELECT 1");
        await cmd.ExecuteScalarAsync(ct);
        _logger.LogInformation("Veritabanı bağlantısı doğrulandı.");
    }

    // ---- Anlık fiyat tablosu (prices / us_prices) ----

    public async Task<IReadOnlyList<string>> GetSymbolsAsync(string pricesTable, CancellationToken ct)
    {
        var symbols = new List<string>();
        await using var cmd = _dataSource.CreateCommand($"SELECT symbol FROM {pricesTable} ORDER BY symbol");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var s = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(s)) symbols.Add(s.Trim());
        }
        return symbols;
    }

    public async Task UpdatePriceAsync(string pricesTable, string symbol, decimal price, CancellationToken ct)
    {
        var sql = $@"
            INSERT INTO {pricesTable} (symbol, price, updated_at)
            VALUES (@symbol, @price, now())
            ON CONFLICT (symbol)
            DO UPDATE SET price = EXCLUDED.price, updated_at = now()";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.AddWithValue("price", price);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Anlık fiyat tablosuna YENİ sembol eklenince ilgili kanala bildirim gönderen
    /// trigger'ı kurar (idempotent). Sadece INSERT'te tetiklenir; servisin kendi UPSERT
    /// güncellemeleri (UPDATE yolu) tetiklemez, döngü oluşmaz.
    /// </summary>
    public async Task EnsureNewSymbolTriggerAsync(MarketDefinition m, CancellationToken ct)
    {
        var sql = $@"
            CREATE OR REPLACE FUNCTION {m.NewSymbolFunction}() RETURNS trigger AS $$
            BEGIN
              PERFORM pg_notify('{m.NewSymbolChannel}', NEW.symbol);
              RETURN NULL;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS {m.NewSymbolTrigger} ON {m.PricesTable};
            CREATE TRIGGER {m.NewSymbolTrigger}
              AFTER INSERT ON {m.PricesTable}
              FOR EACH ROW EXECUTE PROCEDURE {m.NewSymbolFunction}();";

        await using var cmd = _dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("[{Market}] yeni-sembol trigger'ı ({Channel}) hazır.", m.Name, m.NewSymbolChannel);
    }

    // ---- Geçmiş kapanış tablosu (price_history / us_price_history) ----

    public async Task EnsureHistoryTableAsync(string historyTable, CancellationToken ct)
    {
        var sql = $@"
            CREATE TABLE IF NOT EXISTS {historyTable} (
              symbol      TEXT NOT NULL,
              date        DATE NOT NULL,
              close       NUMERIC(18,4),
              adj_close   NUMERIC(18,4),
              updated_at  TIMESTAMPTZ NOT NULL DEFAULT now(),
              PRIMARY KEY (symbol, date)
            );
            CREATE INDEX IF NOT EXISTS idx_{historyTable}_symbol_date ON {historyTable}(symbol, date);";

        await using var cmd = _dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("{Table} tablosu hazır.", historyTable);
    }

    /// <summary>Bir sembolün günlük kapanışlarını toplu UPSERT eder. Yazılan satır sayısı döner.</summary>
    public async Task<int> UpsertHistoryAsync(string historyTable, string symbol, IReadOnlyList<DailyBar> bars, CancellationToken ct)
    {
        if (bars.Count == 0) return 0;

        var dates = new DateOnly[bars.Count];
        var closes = new decimal?[bars.Count];
        var adj = new decimal?[bars.Count];
        for (int i = 0; i < bars.Count; i++)
        {
            dates[i] = bars[i].Date;
            closes[i] = bars[i].Close;
            adj[i] = bars[i].AdjClose;
        }

        var sql = $@"
            INSERT INTO {historyTable} (symbol, date, close, adj_close, updated_at)
            SELECT @symbol, d, c, a, now()
            FROM unnest(@dates, @closes, @adj) AS t(d, c, a)
            ON CONFLICT (symbol, date)
            DO UPDATE SET close = EXCLUDED.close, adj_close = EXCLUDED.adj_close, updated_at = now()";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("symbol", symbol);
        cmd.Parameters.Add(new NpgsqlParameter("dates", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = dates });
        cmd.Parameters.Add(new NpgsqlParameter("closes", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = closes });
        cmd.Parameters.Add(new NpgsqlParameter("adj", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = adj });
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Döviz kuru (fx_rates / fx_rates_history) ----

    /// <summary>Anlık USD/TRY kurunu bugünün tarihiyle yazar (UPSERT).</summary>
    public async Task UpsertFxRateAsync(string ratesTable, DateOnly date, decimal rate, CancellationToken ct)
    {
        var sql = $@"
            INSERT INTO {ratesTable} (date, rate, updated_at)
            VALUES (@date, @rate, now())
            ON CONFLICT (date)
            DO UPDATE SET rate = EXCLUDED.rate, updated_at = now()";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter("date", NpgsqlDbType.Date) { Value = date });
        cmd.Parameters.AddWithValue("rate", rate);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task EnsureFxHistoryTableAsync(string historyTable, CancellationToken ct)
    {
        var sql = $@"
            CREATE TABLE IF NOT EXISTS {historyTable} (
              date        DATE NOT NULL PRIMARY KEY,
              rate        NUMERIC(18,6) NOT NULL,
              updated_at  TIMESTAMPTZ NOT NULL DEFAULT now()
            );";

        await using var cmd = _dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("{Table} tablosu hazır.", historyTable);
    }

    /// <summary>Günlük USD/TRY kapanışlarını toplu UPSERT eder. Yazılan satır sayısı döner.</summary>
    public async Task<int> UpsertFxHistoryAsync(string historyTable, IReadOnlyList<DailyBar> bars, CancellationToken ct)
    {
        // Kapanışı olmayan günleri at.
        var valid = bars.Where(b => b.Close is not null).ToList();
        if (valid.Count == 0) return 0;

        var dates = new DateOnly[valid.Count];
        var rates = new decimal[valid.Count];
        for (int i = 0; i < valid.Count; i++)
        {
            dates[i] = valid[i].Date;
            rates[i] = valid[i].Close!.Value;
        }

        var sql = $@"
            INSERT INTO {historyTable} (date, rate, updated_at)
            SELECT d, r, now()
            FROM unnest(@dates, @rates) AS t(d, r)
            ON CONFLICT (date)
            DO UPDATE SET rate = EXCLUDED.rate, updated_at = now()";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.Add(new NpgsqlParameter("dates", NpgsqlDbType.Array | NpgsqlDbType.Date) { Value = dates });
        cmd.Parameters.Add(new NpgsqlParameter("rates", NpgsqlDbType.Array | NpgsqlDbType.Numeric) { Value = rates });
        return await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Kıymetli maden (metal_prices) ----

    /// <summary>Bir madenin (gold/silver) gram-TL fiyatını UPSERT eder.</summary>
    public async Task UpsertMetalAsync(string table, string metal, decimal price, CancellationToken ct)
    {
        var sql = $@"
            INSERT INTO {table} (metal, price, updated_at)
            VALUES (@metal, @price, now())
            ON CONFLICT (metal)
            DO UPDATE SET price = EXCLUDED.price, updated_at = now()";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("metal", metal);
        cmd.Parameters.AddWithValue("price", price);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- Döviz (currency_prices) ----

    /// <summary>Bir dövizin (eur/usd ...) TL fiyatını UPSERT eder.</summary>
    public async Task UpsertCurrencyAsync(string table, string currency, decimal price, CancellationToken ct)
    {
        var sql = $@"
            INSERT INTO {table} (currency, price, updated_at)
            VALUES (@currency, @price, now())
            ON CONFLICT (currency)
            DO UPDATE SET price = EXCLUDED.price, updated_at = now()";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("currency", currency);
        cmd.Parameters.AddWithValue("price", price);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    // ---- TEFAS fonları (fund_prices) ----

    /// <summary>
    /// fund_prices'a price_old kolonunu ekler (idempotent). Fiyat 0 geldiğinde (fon o
    /// gün henüz fiyatlanmamış) mevcut price buraya taşınır; web tarafı price=0 iken
    /// bu kolondan (bir önceki geçerli fiyat) gösterim yapabilir.
    /// </summary>
    public async Task EnsureFundPriceOldColumnAsync(string table, CancellationToken ct)
    {
        await using var cmd = _dataSource.CreateCommand($"ALTER TABLE {table} ADD COLUMN IF NOT EXISTS price_old NUMERIC");
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>fund_prices tablosundaki fon kodlarını döndürür (kolon: code).</summary>
    public async Task<IReadOnlyList<string>> GetFundCodesAsync(string table, CancellationToken ct)
    {
        var codes = new List<string>();
        await using var cmd = _dataSource.CreateCommand($"SELECT code FROM {table} ORDER BY code");
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var c = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(c)) codes.Add(c.Trim());
        }
        return codes;
    }

    /// <summary>
    /// fund_prices'a YENİ fon eklenince ilgili kanala bildirim gönderen trigger'ı kurar
    /// (idempotent). Sadece INSERT'te tetiklenir; servisin kendi UPSERT güncellemeleri
    /// (UPDATE yolu) tetiklemez, döngü oluşmaz. Web'in fund_price_change trigger'ından ayrıdır.
    /// </summary>
    public async Task EnsureNewFundTriggerAsync(FundOptions o, CancellationToken ct)
    {
        var sql = $@"
            CREATE OR REPLACE FUNCTION {o.NewFundFunction}() RETURNS trigger AS $$
            BEGIN
              PERFORM pg_notify('{o.NewFundChannel}', NEW.code);
              RETURN NULL;
            END;
            $$ LANGUAGE plpgsql;

            DROP TRIGGER IF EXISTS {o.NewFundTrigger} ON {o.Table};
            CREATE TRIGGER {o.NewFundTrigger}
              AFTER INSERT ON {o.Table}
              FOR EACH ROW EXECUTE PROCEDURE {o.NewFundFunction}();";

        await using var cmd = _dataSource.CreateCommand(sql);
        await cmd.ExecuteNonQueryAsync(ct);
        _logger.LogInformation("[FON] yeni-fon trigger'ı ({Channel}) hazır.", o.NewFundChannel);
    }

    /// <summary>Bir fonun (code) adını ve fiyatını UPSERT eder. price_old da aynı değere yazılır.</summary>
    public async Task UpsertFundAsync(string table, string code, string title, decimal price, CancellationToken ct)
    {
        var sql = $@"
            INSERT INTO {table} (code, title, price, price_old, updated_at)
            VALUES (@code, @title, @price, @price, now())
            ON CONFLICT (code)
            DO UPDATE SET title = EXCLUDED.title, price = EXCLUDED.price, price_old = EXCLUDED.price, updated_at = now()";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("title", title);
        cmd.Parameters.AddWithValue("price", price);
        await cmd.ExecuteNonQueryAsync(ct);
    }

    /// <summary>
    /// Fon o gün için henüz fiyatlanmamışken (fiyat 0) çağrılır: mevcut price değeri
    /// price_old'a taşınır, price 0 olarak yazılır. Yeni (hiç satırı olmayan) fon için
    /// taşınacak eski fiyat olmadığından price_old da 0 ile başlar.
    /// </summary>
    public async Task MarkFundPriceZeroAsync(string table, string code, string title, CancellationToken ct)
    {
        var sql = $@"
            INSERT INTO {table} (code, title, price, price_old, updated_at)
            VALUES (@code, @title, 0, 0, now())
            ON CONFLICT (code)
            DO UPDATE SET title = EXCLUDED.title, price_old = {table}.price, price = 0, updated_at = now()";

        await using var cmd = _dataSource.CreateCommand(sql);
        cmd.Parameters.AddWithValue("code", code);
        cmd.Parameters.AddWithValue("title", title);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
