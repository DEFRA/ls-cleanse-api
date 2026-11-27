namespace Cleanse.Core.Domain.Mi;

/// <summary>Days as the business counts them: UK days, weeks from Monday, calendar months.</summary>
public static class MiCalendar
{
    public static DateOnly UkDay(DateTimeOffset at) => DateOnly.FromDateTime(UkTime.ToUk(at).DateTime);

    /// <summary>A band of ages in whole days, both ends included.</summary>
    public sealed record Band(string Id, int FromDays, int ToDays)
    {
        public bool Contains(int days) => days >= FromDays && days <= ToDays;
    }

    /// <summary>How long ago an issue was raised, or how long it took to reach an outcome.</summary>
    public static readonly IReadOnlyList<Band> AgeBands =
    [
        new("under-1-week", 0, 6),
        new("1-2-weeks", 7, 13),
        new("2-4-weeks", 14, 27),
        new("1-2-months", 28, 59),
        new("2-3-months", 60, 89),
        new("3-6-months", 90, 179),
        new("over-6-months", 180, int.MaxValue)
    ];

    /// <summary>How many actions a holding had when it closed.</summary>
    public static readonly IReadOnlyList<Band> ActionBands =
    [
        new("0", 0, 0),
        new("1", 1, 1),
        new("2", 2, 2),
        new("3", 3, 3),
        new("4", 4, 4),
        new("5-or-more", 5, int.MaxValue)
    ];

    public static Band BandOf(IReadOnlyList<Band> bands, int value)
    {
        ArgumentNullException.ThrowIfNull(bands);
        return bands.FirstOrDefault(band => band.Contains(value)) ?? bands[^1];
    }

    public static int DaysBetween(DateOnly from, DateOnly to) => to.DayNumber - from.DayNumber;

    /// <summary>
    /// The smallest value at or above the given share of a distribution held as value and count
    /// pairs: the nearest-rank percentile. Null when the distribution is empty.
    /// </summary>
    public static int? Percentile(IEnumerable<(int Value, int Count)> distribution, double share)
    {
        ArgumentNullException.ThrowIfNull(distribution);
        var sorted = distribution.Where(d => d.Count > 0).OrderBy(d => d.Value).ToList();
        var total = sorted.Sum(d => (long)d.Count);
        if (total == 0)
        {
            return null;
        }

        var rank = Math.Max(1, (long)Math.Ceiling(share * total));
        long seen = 0;
        foreach (var (value, count) in sorted)
        {
            seen += count;
            if (seen >= rank)
            {
                return value;
            }
        }

        return sorted[^1].Value;
    }
}

/// <summary>One point on a chart's time axis: a day, a week or a month, cut to the period.</summary>
public sealed record MiBucket(DateOnly Start, DateOnly End)
{
    public bool Contains(DateOnly day) => day >= Start && day <= End;
}

/// <summary>The whole UK days a dashboard view covers, and how they are grouped.</summary>
public sealed record MiPeriod(DateOnly From, DateOnly To, MiInterval Interval)
{
    /// <summary>More points than this would not fit a chart, so a longer period needs a longer interval.</summary>
    public const int MaxPoints = 400;

    public const int DefaultDays = 7;

    public int Days => To.DayNumber - From.DayNumber + 1;

    /// <summary>The period of the same length just before this one.</summary>
    public MiPeriod Previous() => this with { From = From.AddDays(-Days), To = From.AddDays(-1) };

    public static MiInterval DefaultInterval(int days) =>
        days <= 31 ? MiInterval.Day : days <= 366 ? MiInterval.Week : MiInterval.Month;

    public IReadOnlyList<MiBucket> Buckets()
    {
        var buckets = new List<MiBucket>();
        for (var start = From; start <= To;)
        {
            var end = Interval switch
            {
                MiInterval.Day => start,
                // DayOfWeek counts from Sunday; a week here starts on Monday.
                MiInterval.Week => start.AddDays(6 - ((int)start.DayOfWeek + 6) % 7),
                _ => new DateOnly(start.Year, start.Month, DateTime.DaysInMonth(start.Year, start.Month))
            };

            if (end > To)
            {
                end = To;
            }

            buckets.Add(new MiBucket(start, end));
            start = end.AddDays(1);
        }

        return buckets;
    }

    /// <summary>
    /// The period asked for: from and to are both included, and default to the 7 days ending
    /// today. A period reaching back before the history starts is cut to it.
    /// </summary>
    public static Result<MiPeriod> Resolve(
        DateOnly? from, DateOnly? to, string? interval, DateOnly today, DateOnly? historyFrom)
    {
        var end = to ?? today;
        if (end > today)
        {
            return DomainErrors.Validation("The period cannot end after today.");
        }

        var start = from ?? end.AddDays(-(DefaultDays - 1));
        if (start > end)
        {
            return DomainErrors.Validation("The period must start on or before the day it ends.");
        }

        if (historyFrom is { } first && start < first && end >= first)
        {
            start = first;
        }

        var days = end.DayNumber - start.DayNumber + 1;
        MiInterval chosen;
        if (string.IsNullOrWhiteSpace(interval))
        {
            chosen = DefaultInterval(days);
        }
        else if (!MiNames.TryParseInterval(interval, out chosen))
        {
            return DomainErrors.Validation($"'{interval}' is not an interval. Use day, week or month.");
        }

        var period = new MiPeriod(start, end, chosen);
        return period.Buckets().Count > MaxPoints
            ? DomainErrors.Validation(
                $"A period of {days} days by {chosen.ToWire()} has more than {MaxPoints} points. Choose a longer interval.")
            : period;
    }
}
