namespace BistPriceService;

/// <summary>Saat dilimi çözümü için ortak yardımcı (Windows / Linux uyumlu).</summary>
public static class TimeZoneHelper
{
    public static TimeZoneInfo Resolve(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById("Europe/Istanbul"); }
            catch
            {
                // Son çare: UTC+3 sabiti.
                return TimeZoneInfo.CreateCustomTimeZone("TR", TimeSpan.FromHours(3), "Turkey", "Turkey");
            }
        }
    }
}
