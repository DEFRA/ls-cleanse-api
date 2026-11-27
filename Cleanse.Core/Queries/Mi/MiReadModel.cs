using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;
using Cleanse.Core.Domain.Mi;

namespace Cleanse.Core.Queries.Mi;

/// <summary>What the dashboard's queries and its refresh share.</summary>
internal static class MiReadModel
{
    public const string NotNamed = "Not named";

    public static DomainError? CheckGroup(string? groupId) =>
        string.IsNullOrWhiteSpace(groupId) ? DomainErrors.Validation("A group id is required.") : null;

    /// <summary>Every known user's current name, by id.</summary>
    public static async Task<Dictionary<Guid, string>> LoadPeopleAsync(IUserStore users, CancellationToken cancellationToken)
    {
        var people = new Dictionary<Guid, string>();
        for (var pageNumber = 1; ; pageNumber++)
        {
            var page = await users.SearchAsync(new PageRequest(pageNumber, PageRequest.MaxPageSize), cancellationToken);
            foreach (var user in page.Items)
            {
                people[user.Id] = user.FullName;
            }

            if ((long)pageNumber * PageRequest.MaxPageSize >= page.TotalCount)
            {
                return people;
            }
        }
    }

    public static MiStatusDto Status(string groupId, MiRefreshRecord? refresh, DateTimeOffset now, CleanseCoreOptions options) =>
        refresh is null
            ? new MiStatusDto(groupId, false, null, null, null, null, null, null, null, false)
            : new MiStatusDto(
                groupId,
                true,
                refresh.CheckedAtUtc,
                refresh.BuiltAtUtc,
                refresh.HistoryFromUtc,
                refresh.DurationMilliseconds,
                refresh.Issues,
                refresh.Events,
                refresh.Mismatches,
                now - refresh.CheckedAtUtc > options.MiStaleAfter);

    public static DateOnly? HistoryFromDay(MiRefreshRecord? refresh) =>
        refresh?.HistoryFromUtc is { } from ? MiCalendar.UkDay(from) : null;

    public static MiPeriodDto ToDto(MiPeriod period, DateOnly? historyFromDay)
    {
        var previous = period.Previous();
        return new MiPeriodDto(
            period.From,
            period.To,
            period.Interval.ToWire(),
            period.Days,
            previous.From,
            previous.To,
            historyFromDay is { } first && previous.To >= first);
    }

    public static IReadOnlyList<MiBucketDto> ToDto(IReadOnlyList<MiBucket> buckets) =>
        [.. buckets.Select(bucket => new MiBucketDto(bucket.Start, bucket.End))];

    /// <summary>A value for each bucket: the sum of the counts on its days.</summary>
    public static int[] PerBucket(IReadOnlyList<MiBucket> buckets, IEnumerable<MiEventCount> counts)
    {
        var values = new int[buckets.Count];
        foreach (var count in counts)
        {
            if (count.Day is { } day && IndexOf(buckets, day) is { } index)
            {
                values[index] += count.Count;
            }
        }

        return values;
    }

    public static int? IndexOf(IReadOnlyList<MiBucket> buckets, DateOnly day)
    {
        int low = 0, high = buckets.Count - 1;
        while (low <= high)
        {
            var middle = (low + high) / 2;
            if (day < buckets[middle].Start)
            {
                high = middle - 1;
            }
            else if (day > buckets[middle].End)
            {
                low = middle + 1;
            }
            else
            {
                return middle;
            }
        }

        return null;
    }

    /// <summary>When SPOL imports finished in the period, and where the history starts, if it is in it.</summary>
    public static async Task<IReadOnlyList<MiMarkerDto>> MarkersAsync(
        IImportJobStore imports,
        string groupId,
        MiPeriod period,
        DateTimeOffset? historyFromUtc,
        CancellationToken cancellationToken)
    {
        const int pageSize = 100;
        const int maxPages = 20;

        var markers = new List<MiMarkerDto>();
        if (historyFromUtc is { } first && MiCalendar.UkDay(first) is var firstDay && firstDay >= period.From && firstDay <= period.To)
        {
            markers.Add(new MiMarkerDto(first, firstDay, "history-start"));
        }

        for (var pageNumber = 1; pageNumber <= maxPages; pageNumber++)
        {
            var page = await imports.SearchAsync(groupId, new PageRequest(pageNumber, pageSize), cancellationToken: cancellationToken);
            foreach (var job in page.Items)
            {
                if (job.Status == ImportJobStatus.Completed &&
                    job.FinishedAtUtc is { } finished &&
                    MiCalendar.UkDay(finished) is var day && day >= period.From && day <= period.To)
                {
                    markers.Add(new MiMarkerDto(finished, day, "spol-import"));
                }
            }

            // Newest first, so once a page reaches back before the period the rest are older still.
            var reachedBefore = page.Items.Count > 0 && MiCalendar.UkDay(page.Items[^1].CreatedAtUtc) < period.From;
            if (reachedBefore || (long)pageNumber * pageSize >= page.TotalCount)
            {
                break;
            }
        }

        return [.. markers.OrderBy(marker => marker.AtUtc)];
    }

    public static string NameOf(Guid? userId, IReadOnlyDictionary<Guid, string> people) =>
        userId is { } id && people.TryGetValue(id, out var name) ? name : NotNamed;
}
