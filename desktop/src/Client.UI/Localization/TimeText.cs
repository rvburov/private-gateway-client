using System.Globalization;

namespace PrivateGateway.Client.UI.Localization;

/// <summary>Время в подписях: «в 18:10», «вчера», «22 окт.»; «5 мин назад», «2 ч назад». Часы — программы.</summary>
public static class TimeText
{
    /// <summary>Когда: сегодня — время, вчера — «вчера», раньше — дата.</summary>
    public static string Day(DateTimeOffset at, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var local = TimeZoneInfo.ConvertTime(at, time.LocalTimeZone);
        var today = TimeZoneInfo.ConvertTime(time.GetUtcNow(), time.LocalTimeZone).Date;
        return local.Date == today ? Loc.Instance.Format("when.at", local.ToString("HH:mm", CultureInfo.InvariantCulture))
            : local.Date == today.AddDays(-1) ? Loc.Instance["when.yesterday"]
            : local.ToString("d MMM", Loc.Instance.Culture);
    }

    /// <summary>Сколько прошло: минуты, часы или дни.</summary>
    public static string Ago(DateTimeOffset at, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(time);
        var span = time.GetUtcNow() - at;
        return span < TimeSpan.FromMinutes(1) ? Loc.Instance["ago.now"]
            : span < TimeSpan.FromHours(1) ? Loc.Instance.Format("ago.minutes", (int)span.TotalMinutes)
            : span < TimeSpan.FromDays(1) ? Loc.Instance.Format("ago.hours", (int)span.TotalHours)
            : Loc.Instance.Format("ago.days", (int)span.TotalDays);
    }
}
