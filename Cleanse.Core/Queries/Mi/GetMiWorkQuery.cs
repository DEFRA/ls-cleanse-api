using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Mi;

namespace Cleanse.Core.Queries.Mi;

/// <summary>When the dashboard's figures were last brought up to date, and from what.</summary>
public sealed record GetMiStatusQuery(string GroupId) : IQuery<MiStatusDto>;

internal sealed class GetMiStatusQueryHandler(IMiStore store, TimeProvider clock, CleanseCoreOptions options)
    : IQueryHandler<GetMiStatusQuery, MiStatusDto>
{
    public async Task<Result<MiStatusDto>> HandleAsync(GetMiStatusQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (MiReadModel.CheckGroup(query.GroupId) is { } invalid)
        {
            return invalid;
        }

        var groupId = query.GroupId.Trim();
        var refresh = await store.GetRefreshAsync(groupId, cancellationToken);
        return MiReadModel.Status(groupId, refresh, clock.GetUtcNow(), options);
    }
}

/// <summary>
/// The work done in Cleanse over a period: outcomes, actions and notes, by person, type, reason
/// and rule, each compared with the period before. Only changes people made in Cleanse count.
/// </summary>
public sealed record GetMiWorkQuery(
    string GroupId,
    DateOnly? From = null,
    DateOnly? To = null,
    string? Interval = null,
    string? RuleId = null,
    Guid? UserId = null) : IQuery<MiWorkDto>;

internal sealed class GetMiWorkQueryHandler(
    IMiStore store,
    IUserStore users,
    ICodedValueStore codedValues,
    IImportJobStore imports,
    TimeProvider clock,
    CleanseCoreOptions options) : IQueryHandler<GetMiWorkQuery, MiWorkDto>
{
    private const string Resolved = StatusNames.Resolved;
    private const string CantResolve = StatusNames.Nonactionable;

    private static readonly MiSource[] Cleanse = [MiSource.Cleanse];

    public async Task<Result<MiWorkDto>> HandleAsync(GetMiWorkQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (MiReadModel.CheckGroup(query.GroupId) is { } invalid)
        {
            return invalid;
        }

        var groupId = query.GroupId.Trim();
        var ruleId = string.IsNullOrWhiteSpace(query.RuleId) ? null : query.RuleId.Trim();
        var now = clock.GetUtcNow();
        var refresh = await store.GetRefreshAsync(groupId, cancellationToken);
        var historyFrom = MiReadModel.HistoryFromDay(refresh);

        var resolved = MiPeriod.Resolve(query.From, query.To, query.Interval, MiCalendar.UkDay(now), historyFrom);
        if (resolved.IsFailure)
        {
            return resolved.Error;
        }

        var period = resolved.Value;
        var previous = period.Previous();
        var buckets = period.Buckets();
        var people = await MiReadModel.LoadPeopleAsync(users, cancellationToken);

        var both = new MiEventFilter(groupId, previous.From, period.To) { UserId = query.UserId };
        var issueWork = both with { Units = [MiUnit.Issue], Kinds = [MiEventKind.Workflow], Sources = Cleanse, RuleId = ruleId };
        var inPeriod = issueWork with { FromDay = period.From };

        var workflow = await store.CountEventsAsync(
            issueWork, MiEventDimensions.Day | MiEventDimensions.User | MiEventDimensions.FromWorkflow | MiEventDimensions.ToWorkflow,
            cancellationToken);
        var reasons = await store.CountEventsAsync(
            issueWork, MiEventDimensions.Day | MiEventDimensions.ToWorkflow | MiEventDimensions.Reason | MiEventDimensions.InactiveNow,
            cancellationToken);
        var elapsed = await store.CountEventsAsync(
            inPeriod, MiEventDimensions.ToWorkflow | MiEventDimensions.ElapsedDays, cancellationToken);
        var byRule = await store.CountEventsAsync(
            inPeriod, MiEventDimensions.ToWorkflow | MiEventDimensions.Rule, cancellationToken);
        var actions = await store.CountEventsAsync(
            both with { Units = [MiUnit.Action] },
            MiEventDimensions.Day | MiEventDimensions.User | MiEventDimensions.Reason, cancellationToken);
        var notes = await store.CountEventsAsync(
            both with { Units = [MiUnit.Note] }, MiEventDimensions.Day | MiEventDimensions.User, cancellationToken);
        var closures = await store.CountEventsAsync(
            both with { Units = [MiUnit.Holding], Kinds = [MiEventKind.HoldingStatus], Sources = Cleanse },
            MiEventDimensions.Day | MiEventDimensions.User | MiEventDimensions.FromWorkflow | MiEventDimensions.ToWorkflow |
            MiEventDimensions.ActionsSinceClosed,
            cancellationToken);

        var worked = both with { Units = [MiUnit.Issue, MiUnit.Action, MiUnit.Note], Sources = Cleanse };
        var workedNow = await store.CountEventsAsync(
            worked with { FromDay = period.From }, MiEventDimensions.User | MiEventDimensions.Cph, cancellationToken);
        var workedBefore = await store.CountEventsAsync(
            worked with { ToDay = previous.To }, MiEventDimensions.Cph, cancellationToken);
        var lastActive = await store.CountEventsAsync(
            worked with { FromDay = DateOnly.MinValue, ToDay = DateOnly.MaxValue }, MiEventDimensions.User, cancellationToken);

        var holdings = await store.CountHoldingStatesAsync(groupId, cancellationToken);
        var actionTypes = await codedValues.ListAsync(CodedList.ActionType, cancellationToken);
        var resolvedReasons = await codedValues.ListAsync(CodedList.ResolvedReason, cancellationToken);
        var cantResolveReasons = await codedValues.ListAsync(CodedList.CantResolveReason, cancellationToken);
        var markers = await MiReadModel.MarkersAsync(imports, groupId, period, refresh?.HistoryFromUtc, cancellationToken);

        bool Current(MiEventCount count) => count.Day >= period.From;
        bool Previous(MiEventCount count) => count.Day <= previous.To;

        var outcomes = workflow.Where(count => count.ToWorkflow is Resolved or CantResolve).ToList();
        var resolvedCounts = workflow.Where(count => count.ToWorkflow == Resolved).ToList();
        var cantResolveCounts = workflow.Where(count => count.ToWorkflow == CantResolve).ToList();
        var reopened = workflow.Where(count => count.FromWorkflow is Resolved or CantResolve &&
                                               count.ToWorkflow is StatusNames.Todo or StatusNames.InProgress).ToList();
        var suppressed = workflow.Where(count => count.ToWorkflow == StatusNames.Suppressed).ToList();
        var closed = closures.Where(count => count.ToWorkflow == HoldingStatuses.Closed &&
                                             count.FromWorkflow is not null and not HoldingStatuses.Closed).ToList();

        var openAssigned = holdings
            .Where(count => count.Status is HoldingStatus.Todo or HoldingStatus.InProgress)
            .GroupBy(count => count.AssignedUserId)
            .ToDictionary(group => group.Key ?? Guid.Empty, group => group.Sum(count => count.Count));
        var unassigned = openAssigned.GetValueOrDefault(Guid.Empty);
        if (query.UserId is { } only)
        {
            openAssigned = openAssigned.Where(entry => entry.Key == only).ToDictionary();
        }

        var headline = new MiWorkHeadlineDto(
            Figure(buckets, resolvedCounts, Current, Previous),
            Figure(buckets, cantResolveCounts, Current, Previous),
            Figure(buckets, closed, Current, Previous),
            Figure(buckets, actions, Current, Previous),
            PeopleWorking(buckets, [.. workflow, .. actions, .. notes], Current, Previous),
            new MiFigureDto(
                workedNow.Select(count => count.Cph).Distinct(StringComparer.Ordinal).Count(),
                workedBefore.Select(count => count.Cph).Distinct(StringComparer.Ordinal).Count(),
                []),
            Confirmed(reasons, Current, Previous));

        var outcomePoints = buckets.Select(bucket => new MiOutcomePointDto(
            bucket.Start,
            bucket.End,
            Sum(resolvedCounts, bucket),
            Sum(cantResolveCounts, bucket),
            Sum(reopened, bucket),
            Sum(suppressed, bucket))).ToList();

        var (personRows, team) = People(
            buckets, people, openAssigned, unassigned,
            [.. outcomes.Where(Current)], [.. resolvedCounts.Where(Current)], [.. cantResolveCounts.Where(Current)],
            [.. suppressed.Where(Current)], [.. reopened.Where(Current)], [.. actions.Where(Current)], [.. notes.Where(Current)],
            [.. closed.Where(Current)], workedNow, lastActive);

        var actionsNow = actions.Where(Current).ToList();
        var types = TypeTotals(actionTypes, actionsNow);

        return new MiWorkDto(
            groupId,
            MiReadModel.Status(groupId, refresh, now, options),
            MiReadModel.ToDto(period, historyFrom),
            ruleId,
            query.UserId,
            MiReadModel.ToDto(buckets),
            headline,
            outcomePoints,
            personRows,
            team,
            types,
            ActionsOverTime(buckets, types, actionsNow),
            [.. actionsNow
                .Where(count => count.ReasonId is not null)
                .GroupBy(count => (count.UserId, count.ReasonId!.Value))
                .Select(group => new MiPersonActionDto(group.Key.UserId, group.Key.Value, group.Sum(count => count.Count)))
                .OrderBy(entry => MiReadModel.NameOf(entry.UserId, people), StringComparer.OrdinalIgnoreCase)],
            ActionBands(closed.Where(Current)),
            MiCalendar.Percentile(
                closed.Where(Current).Select(count => (count.ActionsSinceClosed ?? 0, count.Count)), 0.5),
            ReasonTotals(resolvedReasons, reasons.Where(Current), Resolved),
            ReasonTotals(cantResolveReasons, reasons.Where(Current), CantResolve),
            [.. workflow.Where(Current)
                .GroupBy(count => (From: count.FromWorkflow ?? string.Empty, To: count.ToWorkflow ?? string.Empty))
                .Select(group => new MiTransitionDto(group.Key.From, group.Key.To, group.Sum(count => count.Count)))
                .OrderByDescending(transition => transition.Count)],
            TimeToOutcome(elapsed),
            [.. byRule
                .GroupBy(count => count.RuleId ?? string.Empty)
                .Select(group => new MiRuleOutcomeDto(
                    group.Key,
                    group.Where(count => count.ToWorkflow == Resolved).Sum(count => count.Count),
                    group.Where(count => count.ToWorkflow == CantResolve).Sum(count => count.Count)))
                .Where(rule => rule.Resolved + rule.CantResolve > 0)
                .OrderBy(rule => rule.RuleId, StringComparer.Ordinal)],
            markers);
    }

    private static int Sum(IEnumerable<MiEventCount> counts, MiBucket bucket) =>
        counts.Where(count => count.Day is { } day && bucket.Contains(day)).Sum(count => count.Count);

    private static MiFigureDto Figure(
        IReadOnlyList<MiBucket> buckets,
        IReadOnlyCollection<MiEventCount> counts,
        Func<MiEventCount, bool> current,
        Func<MiEventCount, bool> previous) =>
        new(
            counts.Where(current).Sum(count => count.Count),
            counts.Where(previous).Sum(count => count.Count),
            MiReadModel.PerBucket(buckets, counts.Where(current)));

    /// <summary>Named people only: work under the shared password is nobody in particular.</summary>
    private static MiFigureDto PeopleWorking(
        IReadOnlyList<MiBucket> buckets,
        IReadOnlyCollection<MiEventCount> counts,
        Func<MiEventCount, bool> current,
        Func<MiEventCount, bool> previous)
    {
        var named = counts.Where(count => count.UserId is not null).ToList();
        var series = buckets
            .Select(bucket => named
                .Where(count => count.Day is { } day && bucket.Contains(day))
                .Select(count => count.UserId)
                .Distinct()
                .Count())
            .ToList();

        return new MiFigureDto(
            named.Where(current).Select(count => count.UserId).Distinct().Count(),
            named.Where(previous).Select(count => count.UserId).Distinct().Count(),
            series);
    }

    private static MiConfirmedDto Confirmed(
        IReadOnlyCollection<MiEventCount> reasons,
        Func<MiEventCount, bool> current,
        Func<MiEventCount, bool> previous)
    {
        var resolved = reasons.Where(count => count.ToWorkflow == Resolved).ToList();
        return new MiConfirmedDto(
            resolved.Where(current).Sum(count => count.Count),
            resolved.Where(current).Where(count => count.InactiveNow == true).Sum(count => count.Count),
            resolved.Where(previous).Sum(count => count.Count),
            resolved.Where(previous).Where(count => count.InactiveNow == true).Sum(count => count.Count));
    }

    private static (IReadOnlyList<MiPersonDto> People, MiTeamDto Team) People(
        IReadOnlyList<MiBucket> buckets,
        IReadOnlyDictionary<Guid, string> people,
        IReadOnlyDictionary<Guid, int> openAssigned,
        int unassigned,
        IReadOnlyList<MiEventCount> outcomes,
        IReadOnlyList<MiEventCount> resolved,
        IReadOnlyList<MiEventCount> cantResolve,
        IReadOnlyList<MiEventCount> suppressed,
        IReadOnlyList<MiEventCount> reopened,
        IReadOnlyList<MiEventCount> actions,
        IReadOnlyList<MiEventCount> notes,
        IReadOnlyList<MiEventCount> closed,
        IReadOnlyList<MiEventCount> worked,
        IReadOnlyList<MiEventCount> lastActive)
    {
        static int Of(IEnumerable<MiEventCount> counts, Guid? userId) =>
            counts.Where(count => count.UserId == userId).Sum(count => count.Count);

        var withWork = outcomes.Concat(suppressed).Concat(reopened).Concat(actions).Concat(notes).Concat(worked)
            .Select(count => count.UserId)
            .ToHashSet();
        var everyone = withWork
            .Concat(openAssigned.Keys.Where(id => id != Guid.Empty).Select(id => (Guid?)id))
            .Distinct()
            .ToList();

        var rows = everyone
            .Select(userId => new MiPersonDto(
                userId,
                MiReadModel.NameOf(userId, people),
                userId is not null,
                Of(resolved, userId),
                Of(cantResolve, userId),
                Of(suppressed, userId),
                Of(reopened, userId),
                Of(actions, userId),
                Of(notes, userId),
                worked.Where(count => count.UserId == userId).Select(count => count.Cph).Distinct(StringComparer.Ordinal).Count(),
                Of(closed, userId),
                userId is { } id ? openAssigned.GetValueOrDefault(id) : 0,
                lastActive.FirstOrDefault(count => count.UserId == userId)?.LastAtUtc,
                MiReadModel.PerBucket(buckets, outcomes.Where(count => count.UserId == userId))))
            .OrderBy(row => row.Named ? 0 : 1)
            .ThenBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var team = new MiTeamDto(
            rows.Count(row => row.Named && withWork.Contains(row.UserId)),
            rows.Sum(row => row.Resolved),
            rows.Sum(row => row.CantResolve),
            rows.Sum(row => row.Suppressed),
            rows.Sum(row => row.Reopened),
            rows.Sum(row => row.Actions),
            rows.Sum(row => row.Notes),
            worked.Select(count => count.Cph).Distinct(StringComparer.Ordinal).Count(),
            rows.Sum(row => row.HoldingsClosed),
            openAssigned.Where(entry => entry.Key != Guid.Empty).Sum(entry => entry.Value),
            unassigned);

        return (rows, team);
    }

    /// <summary>
    /// Every active entry, used or not, then any entry no longer offered that was used. Sorted
    /// largest first, so the first few are the ones that matter.
    /// </summary>
    private static List<MiListEntryDto> TypeTotals(IReadOnlyList<CodedValue> list, IReadOnlyList<MiEventCount> counts)
    {
        var used = counts
            .Where(count => count.ReasonId is not null)
            .GroupBy(count => count.ReasonId!.Value)
            .ToDictionary(group => group.Key, group => (Count: group.Sum(count => count.Count), Name: group.First().ReasonName));
        var known = list.ToDictionary(value => value.Id);

        var entries = list
            .Where(value => value.IsActive || used.ContainsKey(value.Id))
            .Select(value => new MiListEntryDto(value.Id, value.Name, value.IsActive, used.GetValueOrDefault(value.Id).Count))
            .Concat(used
                .Where(entry => !known.ContainsKey(entry.Key))
                .Select(entry => new MiListEntryDto(entry.Key, entry.Value.Name ?? entry.Key.ToString(), false, entry.Value.Count)));

        return [.. entries.OrderByDescending(entry => entry.Count).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The most used types each get a series; the rest share one.</summary>
    private static IReadOnlyList<MiSeriesDto> ActionsOverTime(
        IReadOnlyList<MiBucket> buckets,
        IReadOnlyList<MiListEntryDto> types,
        IReadOnlyList<MiEventCount> counts)
    {
        const int shown = 5;
        var top = types.Where(type => type.Count > 0).Take(shown).ToList();
        var topIds = top.Select(type => type.Id).ToHashSet();

        var series = top
            .Select(type => new MiSeriesDto(
                type.Id, type.Name, MiReadModel.PerBucket(buckets, counts.Where(count => count.ReasonId == type.Id))))
            .ToList();

        var rest = counts.Where(count => !topIds.Contains(count.ReasonId)).ToList();
        if (rest.Sum(count => count.Count) > 0)
        {
            series.Add(new MiSeriesDto(null, "Other", MiReadModel.PerBucket(buckets, rest)));
        }

        return series;
    }

    private static IReadOnlyList<MiBandDto> ActionBands(IEnumerable<MiEventCount> closures)
    {
        var bands = MiCalendar.ActionBands.ToDictionary(band => band.Id, _ => 0, StringComparer.Ordinal);
        foreach (var count in closures)
        {
            bands[MiCalendar.BandOf(MiCalendar.ActionBands, count.ActionsSinceClosed ?? 0).Id] += count.Count;
        }

        return [.. MiCalendar.ActionBands.Select(band => new MiBandDto(band.Id, bands[band.Id]))];
    }

    private static IReadOnlyList<MiReasonDto> ReasonTotals(
        IReadOnlyList<CodedValue> list, IEnumerable<MiEventCount> counts, string outcome)
    {
        var given = counts.Where(count => count.ToWorkflow == outcome).ToList();
        var byReason = given
            .GroupBy(count => count.ReasonId)
            .ToDictionary(
                group => group.Key ?? Guid.Empty,
                group => (
                    Count: group.Sum(count => count.Count),
                    NoLongerFound: group.Where(count => count.InactiveNow == true).Sum(count => count.Count),
                    Name: group.First().ReasonName));
        var known = list.ToDictionary(value => value.Id);

        var entries = list
            .Where(value => value.IsActive || byReason.ContainsKey(value.Id))
            .Select(value =>
            {
                var (count, noLongerFound, _) = byReason.GetValueOrDefault(value.Id);
                return new MiReasonDto(value.Id, value.Name, value.IsActive, count, noLongerFound);
            })
            .Concat(byReason
                .Where(entry => entry.Key != Guid.Empty && !known.ContainsKey(entry.Key))
                .Select(entry => new MiReasonDto(
                    entry.Key, entry.Value.Name ?? entry.Key.ToString(), false, entry.Value.Count, entry.Value.NoLongerFound)))
            .ToList();

        if (byReason.TryGetValue(Guid.Empty, out var none) && none.Count > 0)
        {
            entries.Add(new MiReasonDto(null, "No reason given", true, none.Count, none.NoLongerFound));
        }

        return [.. entries.OrderByDescending(entry => entry.Count).ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)];
    }

    private static MiTimeToOutcomeDto TimeToOutcome(IReadOnlyList<MiEventCount> elapsed)
    {
        var outcomes = elapsed.Where(count => count.ToWorkflow is Resolved or CantResolve).ToList();
        var bands = MiCalendar.AgeBands
            .Select(band => new MiOutcomeBandDto(
                band.Id,
                outcomes.Where(count => count.ToWorkflow == Resolved && band.Contains(count.ElapsedDays ?? 0)).Sum(count => count.Count),
                outcomes.Where(count => count.ToWorkflow == CantResolve && band.Contains(count.ElapsedDays ?? 0)).Sum(count => count.Count)))
            .ToList();

        var distribution = outcomes.Select(count => (count.ElapsedDays ?? 0, count.Count)).ToList();
        return new MiTimeToOutcomeDto(
            bands, MiCalendar.Percentile(distribution, 0.5), MiCalendar.Percentile(distribution, 0.85));
    }
}
