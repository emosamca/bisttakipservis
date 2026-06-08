# BIST / US / FX Fiyat Servisi

BIST ve ABD hisselerinin anlık fiyatlarını, günlük kapanış geçmişlerini ve USD/TRY
kurunu **Yahoo Finance**'ten çekip PostgreSQL'e yazar. Fiyat değişince ilgili tablonun
NOTIFY trigger'ı tetiklendiği için web arayüzü anlık güncellenir.

## Mimari

Kod **piyasa-bazlı generic**'tir: bir `MarketDefinition` (BIST, US, ...) için aynı üç
servis ayrı birer örnek olarak çalışır. Yeni bir piyasa eklemek için `appsettings.json`'a
bir kayıt yeterli.

| Bileşen | Görev |
|---------|-------|
| **PriceWorker** | Borsa saatlerinde tüm sembollerin **anlık** fiyatını günceller (her piyasa kendi tablosu/saati/saat dilimiyle) |
| **NewSymbolListener** | Fiyat tablosuna yeni sembol eklenince (borsa saatine bakmadan, hafta sonu dahil) anlık fiyatını + geçmişini hemen çeker |
| **HistoryBackfiller** | Geçmiş kapanış tablosunu doldurur/tamamlar (close + adj_close), günde bir tazeler |
| **FxWorker** | Anlık USD/TRY kurunu `fx_rates`'e (date=bugün) yazar |
| **FxHistoryBackfiller** | `fx_rates_history`'yi günlük kapanışlarla doldurur, günde bir tazeler |

### Tablolar

| Piyasa | Anlık fiyat | Geçmiş | Yeni-sembol kanalı | Yahoo son eki | Saat dilimi/saat |
|--------|-------------|--------|--------------------|---------------|------------------|
| BIST | `prices` | `price_history` | `price_new` | `.IS` | TR 10:00–18:10 |
| US | `us_prices` | `us_price_history` | `us_price_new` | (yok) | Eastern 09:30–16:00 |
| FX (USD/TRY) | `fx_rates` (date,rate) | `fx_rates_history` (date,rate) | — | `USDTRY=X` | TR 10:00–23:30 |

> **ABD borsa saatleri** Eastern Time üzerinden değerlendirilir; böylece ABD'nin yaz/kış
> saati (DST) değişimleri Türkiye saatine **otomatik** uyarlanır.

Geçmiş kapanış tabloları (`price_history`, `us_price_history`) `symbol+date` birincil
anahtarlı; `close` (kapanış) ve `adj_close` (temettü/bölünme düzeltilmiş, geriye dönük
değişebilir) tutar. `HistoryStartDate`'ten bugüne çekilir, eksik günler bir sonraki
taramada tamamlanır, `adj_close` her taramada tazelenir. Borsa kapalı günler Yahoo'da
zaten yoktur, doğal atlanır.

Yeni bir hisse takibe almak için ilgili fiyat tablosuna (`prices` / `us_prices`) sembolü
eklemek yeterli; **anında** anlık fiyat + geçmiş çekilir, ayrıca her periyodik turda güncellenir.

## Yapılandırma (`appsettings.json`)

Global ayarlar `PriceFetch` altında; piyasalar `PriceFetch:Markets` dizisinde, döviz
`PriceFetch:Fx` altında.

**Global**

| Ayar | Varsayılan | Açıklama |
|------|-----------|----------|
| `ConnectionStrings:BistTakip` | — | PostgreSQL bağlantı dizgisi |
| `PriceFetch:RequestTimeoutSeconds` | `15` | HTTP zaman aşımı |
| `PriceFetch:PerSymbolDelayMs` | `250` | Semboller arası bekleme (rate-limit) |

**Her piyasa (`PriceFetch:Markets[]`)**

| Ayar | Açıklama |
|------|----------|
| `Name` | Loglarda görünen ad (BIST/US) |
| `PricesTable` / `HistoryTable` | Anlık fiyat ve geçmiş tablosu adları |
| `NewSymbolChannel` / `NewSymbolTrigger` / `NewSymbolFunction` | Yeni-sembol NOTIFY kanalı/trigger/fonksiyon |
| `SymbolSuffix` | Yahoo son eki (BIST `.IS`, US boş) |
| `TimeZoneId`, `MarketOpen`, `MarketClose` | Borsa saatleri |
| `IntervalSeconds`, `OnlyDuringMarketHours` | Anlık tur sıklığı / saat kısıtı |
| `HistoryStartDate`, `HistoryRefreshHours` | Geçmiş başlangıcı / tazeleme sıklığı |

**Döviz (`PriceFetch:Fx`)**

| Ayar | Varsayılan | Açıklama |
|------|-----------|----------|
| `Pair` | `USDTRY=X` | Yahoo döviz sembolü |
| `RatesTable` / `HistoryTable` | `fx_rates` / `fx_rates_history` | Anlık ve geçmiş tabloları |
| `HistoryStartDate` | `2025-08-01` | Geçmiş başlangıcı |
| `IntervalSeconds`, `MarketOpen`/`MarketClose` | `300`, `10:00`/`23:30` | Anlık kur çekme penceresi |

Ayarlar ortam değişkeniyle de geçilebilir (Windows servisi için pratik):
`ConnectionStrings__BistTakip=...`, `PriceFetch__Fx__IntervalSeconds=120`

## Geliştirme

```powershell
cd BistPriceService
dotnet run                       # konsolda çalıştır
# Borsa saati dışında BIST'i de denemek için (Markets[0] = BIST):
$env:PriceFetch__Markets__0__OnlyDuringMarketHours='false'; dotnet run
```

## Yayınlama (publish)

Tek dosyalık, .NET kurulu olmayan makinede de çalışan sürüm:

```powershell
cd BistPriceService
dotnet publish -c Release -r win-x64 --self-contained `
  -p:PublishSingleFile=true -o C:\BistPriceService
```

Çıktı: `C:\BistPriceService\BistPriceService.exe` (+ `appsettings.json`).

## Windows servisi olarak kurma

Yönetici PowerShell'de:

```powershell
sc.exe create "BistFiyatServisi" binPath= "C:\BistPriceService\BistPriceService.exe" start= auto
sc.exe description "BistFiyatServisi" "BIST hisse fiyatlarini Yahoo Finance'ten ceker."
sc.exe start "BistFiyatServisi"
```

> `binPath=` ile `"..."` arasındaki **boşluk zorunludur** (sc.exe sözdizimi).

Durum / durdurma / kaldırma:

```powershell
sc.exe query   "BistFiyatServisi"
sc.exe stop    "BistFiyatServisi"
sc.exe delete  "BistFiyatServisi"
```

## Loglar

Windows servisi olarak çalışırken bilgi/hata kayıtları **Event Viewer →
Windows Logs → Application** altına, kaynak `BIST/US Fiyat Servisi` ile düşer.
Konsoldan çalıştırıldığında stdout'a yazılır. Loglar piyasa etiketlidir: `[BIST]`,
`[US]`, `[FX]`.
