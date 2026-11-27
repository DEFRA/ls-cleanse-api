using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Queries.Issues;

/// <summary>
/// Turns issues into what the API reports, which means looking up their holdings: an issue's
/// assignee is its holding's. Holdings are fetched once per group, however many issues there are.
/// </summary>
internal sealed class IssueReadModel(IHoldingStore holdings)
{
    public async Task<IssueDto> ToDtoAsync(Issue issue, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issue);
        var key = HoldingKey.For(issue);
        var holding = key is { } found ? await holdings.GetAsync(found, cancellationToken) : null;
        return issue.ToDto(holding);
    }

    public async Task<IReadOnlyList<IssueDto>> ToDtosAsync(
        IReadOnlyCollection<Issue> issues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(issues);
        var loaded = await LoadAsync(issues, cancellationToken);
        return [.. issues.Select(issue => issue.ToDto(Find(loaded, issue)))];
    }

    public async Task<Dictionary<HoldingKey, Holding>> LoadAsync(
        IEnumerable<Issue> issues,
        CancellationToken cancellationToken)
    {
        var loaded = new Dictionary<HoldingKey, Holding>();
        var keys = issues.Select(HoldingKey.For).OfType<HoldingKey>().Distinct();

        foreach (var group in keys.GroupBy(key => key.GroupId, StringComparer.Ordinal))
        {
            var found = await holdings.GetManyAsync(group.Key, [.. group.Select(key => key.Cph)], cancellationToken);
            foreach (var holding in found)
            {
                loaded[holding.Key] = holding;
            }
        }

        return loaded;
    }

    public static Holding? Find(IReadOnlyDictionary<HoldingKey, Holding> loaded, Issue issue) =>
        HoldingKey.For(issue) is { } key && loaded.TryGetValue(key, out var holding) ? holding : null;
}
