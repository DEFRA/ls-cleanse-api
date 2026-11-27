using System.Globalization;

namespace Cleanse.Core.Domain;

/// <summary>UK local time, which SharePoint shows SPOL's dates in and admins read them in.</summary>
public static class UkTime
{
    public static readonly TimeZoneInfo Zone =
        TimeZoneInfo.TryFindSystemTimeZoneById("Europe/London", out var london)
            ? london
            : TimeZoneInfo.FindSystemTimeZoneById("GMT Standard Time");

    public static DateTimeOffset ToUk(DateTimeOffset value) => TimeZoneInfo.ConvertTime(value, Zone);

    /// <summary>The day as an admin would write it, such as 01/04/2026.</summary>
    public static string Day(DateTimeOffset value) =>
        ToUk(value).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
}
