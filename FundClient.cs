using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace BistPriceService;

/// <summary>
/// TEFAS fon fiyatlarını tefas.gov.tr'nin resmi JSON API'sinden çeker.
///   POST https://www.tefas.gov.tr/api/funds/fonFiyatBilgiGetir
///   body: { fonKodu, dil: "TR", periyod: 1 }  (periyod ay cinsinden geriye bakış;
///   API yalnızca {1,3,6,12,36,60} değerlerini kabul eder, 1 en güncel fiyat için yeterli)
/// Not: hangikredi.com üzerinden HTML scraping eskiden kullanılıyordu ama site
/// Cloudflare korumasına alınıp servis IP'sini bloke etmeye başladı (403). TEFAS'ın
/// eski sitesi (Imperva/F5 bot koruması) doğrudan erişilemezken, yeni Next.js
/// altyapısındaki bu API endpoint'i korumasız ve doğrudan JSON döndürüyor.
/// </summary>
public sealed class FundClient
{
    private readonly HttpClient _http;
    private readonly ILogger<FundClient> _logger;

    public FundClient(HttpClient http, ILogger<FundClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public readonly record struct FundQuote(string Title, decimal Price);

    private sealed record FundPriceItem(
        [property: JsonPropertyName("fonKodu")] string FonKodu,
        [property: JsonPropertyName("fonUnvan")] string FonUnvan,
        [property: JsonPropertyName("tarih")] string Tarih,
        [property: JsonPropertyName("fiyat")] decimal Fiyat);

    private sealed record FundPriceResponse(
        [property: JsonPropertyName("errorMessage")] string? ErrorMessage,
        [property: JsonPropertyName("resultList")] List<FundPriceItem>? ResultList);

    /// <summary>Fon kodu için (ad, fiyat) döndürür. Başarısızlıkta null.</summary>
    public async Task<FundQuote?> GetFundAsync(string code, CancellationToken ct)
    {
        var payload = new { fonKodu = code, dil = "TR", periyod = 1 };

        try
        {
            // PostAsJsonAsync "Content-Type: application/json; charset=utf-8" gönderir;
            // TEFAS'ın API gateway'i charset parametresi olan isteklerde "Proxy request
            // failed" (500) döndürüyor. Bu yüzden charset'siz "application/json" ile
            // manuel gönderiliyor.
            using var content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8);
            content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            using var resp = await _http.PostAsync("api/funds/fonFiyatBilgiGetir", content, ct);
            if (!resp.IsSuccessStatusCode)
            {
                _logger.LogWarning("Fon {Code} için HTTP {Status} döndü.", code, (int)resp.StatusCode);
                return null;
            }

            var body = await resp.Content.ReadFromJsonAsync<FundPriceResponse>(ct);
            if (body is null || !string.IsNullOrEmpty(body.ErrorMessage))
            {
                _logger.LogWarning("Fon {Code}: API hata döndürdü ({Error}).", code, body?.ErrorMessage);
                return null;
            }

            if (body.ResultList is not { Count: > 0 } list)
            {
                _logger.LogWarning("Fon {Code}: sonuç boş.", code);
                return null;
            }

            // Sonuçlar tarihe göre artan sıradadır; en güncel olan sonuncusudur.
            // Fiyat 0 gelebilir (fon o gün için henüz fiyatlanmamış); bu durum
            // gerçek bir API yanıtı olduğu için null değil, Price=0 olan bir
            // FundQuote olarak döner — çağıran taraf price_old'a düşürme kararını verir.
            var last = list[^1];
            if (last.Fiyat < 0)
            {
                _logger.LogWarning("Fon {Code}: geçersiz (negatif) fiyat.", code);
                return null;
            }

            return new FundQuote(last.FonUnvan.Trim(), last.Fiyat);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fon {Code} fiyatı alınamadı.", code);
            return null;
        }
    }
}
