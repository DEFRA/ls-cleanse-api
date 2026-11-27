using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Queries.Issues;

public sealed record GetIssueQuery(IssueId IssueId) : IQuery<IssueDto>;

internal sealed class GetIssueQueryHandler(IIssueStore issues, IssueReadModel readModel)
    : IQueryHandler<GetIssueQuery, IssueDto>
{
    public async Task<Result<IssueDto>> HandleAsync(
        GetIssueQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var issue = await issues.GetAsync(query.IssueId, cancellationToken);
        return issue is null
            ? DomainErrors.IssueNotFound(query.IssueId.Value)
            : await readModel.ToDtoAsync(issue, cancellationToken);
    }
}

/// <summary>The triage list. Filter is declarative so every storage provider can honour it.</summary>
public sealed record SearchIssuesQuery(IssueFilter Filter) : IQuery<PagedResult<IssueDto>>;

internal sealed class SearchIssuesQueryHandler(IIssueStore issues, IssueReadModel readModel)
    : IQueryHandler<SearchIssuesQuery, PagedResult<IssueDto>>
{
    public async Task<Result<PagedResult<IssueDto>>> HandleAsync(
        SearchIssuesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Filter);

        var page = await issues.SearchAsync(query.Filter, cancellationToken);
        var items = await readModel.ToDtosAsync(page.Items, cancellationToken);
        return new PagedResult<IssueDto>(items, page.PageNumber, page.PageSize, page.TotalCount);
    }
}

/// <summary>
/// The triage list grouped by CPH. Pages count CPHs, not issues. Each CPH also carries its
/// holding's status and assignee, which come from all of its issues rather than the matching ones.
/// </summary>
public sealed record SearchIssuesByCphQuery(IssueFilter Filter) : IQuery<PagedResult<CphIssuesDto>>;

internal sealed class SearchIssuesByCphQueryHandler(IIssueStore issues, IssueReadModel readModel)
    : IQueryHandler<SearchIssuesByCphQuery, PagedResult<CphIssuesDto>>
{
    /// <summary>The context key the rules engine writes the CPH under.</summary>
    public const string CphKey = HoldingKey.CphContextKey;

    public async Task<Result<PagedResult<CphIssuesDto>>> HandleAsync(
        SearchIssuesByCphQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(query.Filter);

        var page = await issues.SearchGroupedAsync(query.Filter, CphKey, cancellationToken);
        var matched = page.Items.SelectMany(group => group.Issues).ToList();
        var holdings = await readModel.LoadAsync(matched, cancellationToken);

        // A filter may have matched only some of a CPH's issues, and the status needs them all.
        var atHolding = new Dictionary<HoldingKey, List<Issue>>();
        foreach (var byGroup in matched
                     .Select(HoldingKey.For)
                     .OfType<HoldingKey>()
                     .Distinct()
                     .GroupBy(key => key.GroupId, StringComparer.Ordinal))
        {
            var found = await issues.GetForHoldingsAsync(
                byGroup.Key, [.. byGroup.Select(key => key.Cph)], cancellationToken);
            foreach (var issue in found)
            {
                if (HoldingKey.For(issue) is { } key)
                {
                    if (!atHolding.TryGetValue(key, out var list))
                    {
                        atHolding[key] = list = [];
                    }

                    list.Add(issue);
                }
            }
        }

        return page.Map(group =>
        {
            // Without a group filter one CPH can span rule groups; the first issue's group speaks for it.
            var key = group.Issues.Select(HoldingKey.For).FirstOrDefault(found => found is not null);
            IReadOnlyList<Issue> all = key is { } k && atHolding.TryGetValue(k, out var list) ? list : group.Issues;
            var holding = key is { } assignedKey ? holdings.GetValueOrDefault(assignedKey) : null;

            return new CphIssuesDto(
                group.Key,
                group.IssueCount,
                group.LatestUpdatedAtUtc,
                [.. group.Issues.Select(issue => issue.ToDto(IssueReadModel.Find(holdings, issue)))],
                HoldingStatuses.Derive(all).ToWire(),
                holding?.AssignedTo,
                holding?.AssignedUserId,
                HoldingStatuses.SuppressedUntil(all));
        });
    }
}
