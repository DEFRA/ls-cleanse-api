using Cleanse.Core.Domain.Mi;

namespace Cleanse.Core.Test.Mi;

public class MiCalendarTests
{
    private static readonly DateOnly Today = new(2026, 11, 27);

    [Fact]
    public void A_UK_day_follows_British_Summer_Time()
    {
        // 23:30 UTC on 1 July is 00:30 on 2 July in London; in January the two agree.
        Assert.Equal(new DateOnly(2026, 7, 2), MiCalendar.UkDay(new DateTimeOffset(2026, 7, 1, 23, 30, 0, TimeSpan.Zero)));
        Assert.Equal(new DateOnly(2026, 1, 1), MiCalendar.UkDay(new DateTimeOffset(2026, 1, 1, 23, 30, 0, TimeSpan.Zero)));
    }

    [Fact]
    public void The_default_period_is_the_7_days_ending_today_by_day()
    {
        var period = MiPeriod.Resolve(null, null, null, Today, null).Value;

        Assert.Equal(new DateOnly(2026, 11, 21), period.From);
        Assert.Equal(Today, period.To);
        Assert.Equal(MiInterval.Day, period.Interval);
        Assert.Equal(7, period.Buckets().Count);
        Assert.Equal(new MiPeriod(new DateOnly(2026, 11, 14), new DateOnly(2026, 11, 20), MiInterval.Day), period.Previous());
    }

    [Fact]
    public void Weeks_start_on_Monday_and_are_cut_to_the_period()
    {
        // 25 November 2026 is a Wednesday.
        var period = new MiPeriod(new DateOnly(2026, 11, 25), new DateOnly(2026, 12, 10), MiInterval.Week);

        Assert.Equal(
            [
                new MiBucket(new DateOnly(2026, 11, 25), new DateOnly(2026, 11, 29)),
                new MiBucket(new DateOnly(2026, 11, 30), new DateOnly(2026, 12, 6)),
                new MiBucket(new DateOnly(2026, 12, 7), new DateOnly(2026, 12, 10))
            ],
            period.Buckets());
    }

    [Fact]
    public void Months_are_calendar_months_cut_to_the_period()
    {
        var period = new MiPeriod(new DateOnly(2026, 1, 15), new DateOnly(2026, 3, 3), MiInterval.Month);

        Assert.Equal(
            [
                new MiBucket(new DateOnly(2026, 1, 15), new DateOnly(2026, 1, 31)),
                new MiBucket(new DateOnly(2026, 2, 1), new DateOnly(2026, 2, 28)),
                new MiBucket(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 3))
            ],
            period.Buckets());
    }

    [Theory]
    [InlineData(7, MiInterval.Day)]
    [InlineData(31, MiInterval.Day)]
    [InlineData(32, MiInterval.Week)]
    [InlineData(366, MiInterval.Week)]
    [InlineData(367, MiInterval.Month)]
    public void The_interval_defaults_by_length(int days, MiInterval expected)
    {
        var period = MiPeriod.Resolve(Today.AddDays(1 - days), Today, null, Today, null).Value;
        Assert.Equal(expected, period.Interval);
    }

    [Fact]
    public void A_period_reaching_back_before_the_history_is_cut_to_it()
    {
        var historyFrom = new DateOnly(2026, 11, 24);
        var period = MiPeriod.Resolve(null, null, null, Today, historyFrom).Value;

        Assert.Equal(historyFrom, period.From);
        Assert.Equal(4, period.Days);
    }

    [Theory]
    [InlineData("2026-11-20", "2026-11-28", null, "after today")]
    [InlineData("2026-11-20", "2026-11-10", null, "on or before")]
    [InlineData("2025-01-01", null, "day", "more than 400 points")]
    [InlineData(null, null, "fortnight", "not an interval")]
    public void An_invalid_period_says_why(string? from, string? to, string? interval, string message)
    {
        var result = MiPeriod.Resolve(
            from is null ? null : DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture),
            to is null ? null : DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture),
            interval,
            Today,
            null);

        Assert.True(result.IsFailure);
        Assert.Contains(message, result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Percentiles_are_nearest_rank()
    {
        (int, int)[] distribution = [(1, 2), (3, 1), (10, 1)];

        Assert.Equal(1, MiCalendar.Percentile(distribution, 0.5));
        Assert.Equal(10, MiCalendar.Percentile(distribution, 0.85));
        Assert.Null(MiCalendar.Percentile([], 0.5));
    }
}
