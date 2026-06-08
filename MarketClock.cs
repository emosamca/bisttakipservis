namespace BistPriceService;

/// <summary>
/// Bir piyasanın açık olup olmadığını (hafta içi + saat aralığı) değerlendirir.
/// Saat dilimi üzerinden çalıştığı için yaz/kış saati (DST) otomatik uyarlanır;
/// örn. ABD borsa saatleri Eastern Time ile değerlendirilince Türkiye saatine
/// otomatik uyar.
/// </summary>
public sealed class MarketClock
{
    private readonly TimeZoneInfo _tz;
    private readonly TimeOnly _open;
    private readonly TimeOnly _close;

    public MarketClock(string timeZoneId, string open, string close)
    {
        _tz = TimeZoneHelper.Resolve(timeZoneId);
        _open = TimeOnly.Parse(open);
        _close = TimeOnly.Parse(close);
    }

    public bool IsOpen(DateTimeOffset utcNow)
    {
        var local = TimeZoneInfo.ConvertTime(utcNow, _tz);
        if (local.DayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday)
            return false;

        var t = TimeOnly.FromTimeSpan(local.TimeOfDay);
        return t >= _open && t <= _close;
    }
}
