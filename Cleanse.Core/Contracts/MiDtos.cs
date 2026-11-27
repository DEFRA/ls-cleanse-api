namespace Cleanse.Core.Contracts;

/// <summary>
/// How current the dashboard's figures are. <see cref="AsAtUtc"/> is when a refresh last found
/// them up to date; <see cref="BuiltAtUtc"/> is when they were last rebuilt. Stale means no
/// refresh has confirmed them for a while, so the latest changes may be missing. Building means
/// a rebuild has been asked for and has not finished; the figures shown until then are the old ones.
/// </summary>
public sealed record MiStatusDto(
    string GroupId,
    bool Built,
    DateTimeOffset? AsAtUtc,
    DateTimeOffset? BuiltAtUtc,
    DateTimeOffset? HistoryFromUtc,
    int? DurationMilliseconds,
    int? Issues,
    int? Events,
    int? Mismatches,
    bool Stale,
    bool Building = false);

/// <summary>
/// The whole UK days shown, both ends included, and the period of the same length before them.
/// <see cref="HasPrevious"/> is false when that earlier period is before the history starts.
/// </summary>
public sealed record MiPeriodDto(
    DateOnly From,
    DateOnly To,
    string Interval,
    int Days,
    DateOnly PreviousFrom,
    DateOnly PreviousTo,
    bool HasPrevious);

/// <summary>One point on the time axis: a day, a week from Monday or a calendar month, cut to the period.</summary>
public sealed record MiBucketDto(DateOnly Start, DateOnly End);

/// <summary>A headline figure, the same figure for the period before, and its value at each point.</summary>
public sealed record MiFigureDto(int Value, int Previous, IReadOnlyList<int> Series);

/// <summary>Issues resolved in the period, and how many of them the rules no longer find.</summary>
public sealed record MiConfirmedDto(int Resolved, int NoLongerFound, int PreviousResolved, int PreviousNoLongerFound);

public sealed record MiWorkHeadlineDto(
    MiFigureDto Resolved,
    MiFigureDto CantResolve,
    MiFigureDto HoldingsClosed,
    MiFigureDto Actions,
    MiFigureDto PeopleWorking,
    MiFigureDto HoldingsWorked,
    MiConfirmedDto Confirmed);

/// <summary>Outcomes, reopenings and suppressions made in Cleanse at one point.</summary>
public sealed record MiOutcomePointDto(DateOnly Start, DateOnly End, int Resolved, int CantResolve, int Reopened, int Suppressed);

/// <summary>
/// One person's work in the period. A null <see cref="UserId"/> is work recorded against the
/// service rather than a named person. <see cref="Outcomes"/> has a value for each point.
/// </summary>
public sealed record MiPersonDto(
    Guid? UserId,
    string Name,
    bool Named,
    int Resolved,
    int CantResolve,
    int Suppressed,
    int Reopened,
    int Actions,
    int Notes,
    int HoldingsWorked,
    int HoldingsClosed,
    int OpenHoldingsAssigned,
    DateTimeOffset? LastActiveUtc,
    IReadOnlyList<int> Outcomes);

/// <summary>Everyone's work together. Each holding worked counts once, however many people worked it.</summary>
public sealed record MiTeamDto(
    int People,
    int Resolved,
    int CantResolve,
    int Suppressed,
    int Reopened,
    int Actions,
    int Notes,
    int HoldingsWorked,
    int HoldingsClosed,
    int OpenHoldingsAssigned,
    int UnassignedOpenHoldings);

/// <summary>An entry in an admin list, or "No reason given", and how often it was used in the period.</summary>
public sealed record MiListEntryDto(Guid? Id, string Name, bool Active, int Count);

/// <summary>An outcome reason, how often it was given, and how many of those issues the rules no longer find.</summary>
public sealed record MiReasonDto(Guid? Id, string Name, bool Active, int Count, int NoLongerFound);

/// <summary>A series with a value for each point. A null id is everything not shown on its own.</summary>
public sealed record MiSeriesDto(Guid? Id, string Name, IReadOnlyList<int> Points);

public sealed record MiPersonActionDto(Guid? UserId, Guid ActionTypeId, int Count);

public sealed record MiBandDto(string Band, int Count);

/// <summary>How many workflow status changes went from one status to another.</summary>
public sealed record MiTransitionDto(string From, string To, int Count);

public sealed record MiOutcomeBandDto(string Band, int Resolved, int CantResolve);

public sealed record MiTimeToOutcomeDto(IReadOnlyList<MiOutcomeBandDto> Bands, int? MedianDays, int? Percentile85Days);

public sealed record MiRuleOutcomeDto(string RuleId, int Resolved, int CantResolve);

/// <summary>Something worth marking on a time chart: <c>spol-import</c> or <c>history-start</c>.</summary>
public sealed record MiMarkerDto(DateTimeOffset AtUtc, DateOnly Day, string Kind);

/// <summary>
/// The work done in Cleanse: everything on the Overview, People, Actions and Outcomes tabs except
/// the open figures. Work counts only changes people made in Cleanse.
/// </summary>
public sealed record MiWorkDto(
    string GroupId,
    MiStatusDto Status,
    MiPeriodDto Period,
    string? RuleId,
    Guid? UserId,
    IReadOnlyList<MiBucketDto> Buckets,
    MiWorkHeadlineDto Headline,
    IReadOnlyList<MiOutcomePointDto> Outcomes,
    IReadOnlyList<MiPersonDto> People,
    MiTeamDto Team,
    IReadOnlyList<MiListEntryDto> ActionTypes,
    IReadOnlyList<MiSeriesDto> ActionsOverTime,
    IReadOnlyList<MiPersonActionDto> ActionsByPerson,
    IReadOnlyList<MiBandDto> ActionsPerHoldingClosed,
    int? MedianActionsPerHoldingClosed,
    IReadOnlyList<MiReasonDto> ResolvedByReason,
    IReadOnlyList<MiReasonDto> CantResolveByReason,
    IReadOnlyList<MiTransitionDto> Transitions,
    MiTimeToOutcomeDto TimeToOutcome,
    IReadOnlyList<MiRuleOutcomeDto> OutcomesByRule,
    IReadOnlyList<MiMarkerDto> Markers);

/// <summary>How many were in each state at the end of a point.</summary>
public sealed record MiStatePointDto(DateOnly Start, DateOnly End, IReadOnlyDictionary<string, int> Counts);

/// <summary>
/// Active and inactive at the end of a point, and the rules' changes during it. For holdings,
/// active means with an active issue, raised means given its first issue, came back means given
/// an active issue again and no longer found means losing its last one.
/// </summary>
public sealed record MiActivityPointDto(
    DateOnly Start,
    DateOnly End,
    int Active,
    int Inactive,
    int Raised,
    int CameBack,
    int NoLongerFound);

/// <summary>One reason the number open went up, down, or (for SPOL imports and corrections) both.</summary>
public sealed record MiWaterfallStepDto(string Reason, int Up, int Down);

/// <summary>The number open at the start, each reason it changed, and the number open at the end.</summary>
public sealed record MiWaterfallDto(int Start, int End, IReadOnlyList<MiWaterfallStepDto> Steps);

/// <summary>Open work by how long ago it was first raised. Too new to list is not yet in the Issues views.</summary>
public sealed record MiAgeBandDto(string Band, int Todo, int InProgress, int TooNewToList);

public sealed record MiRuleOpenDto(string RuleId, int Todo, int InProgress);

public sealed record MiHoldingRowDto(
    string Cph,
    string Status,
    int OpenIssues,
    DateTimeOffset? FirstRaisedAtUtc,
    Guid? AssignedUserId,
    string? AssignedTo,
    DateTimeOffset? LastWorkedAtUtc,
    DateTimeOffset? InProgressSinceUtc);

/// <summary>In progress holdings nobody has worked on in Cleanse for <see cref="Days"/> days or more.</summary>
public sealed record MiStalledDto(int Days, int Count, IReadOnlyList<MiHoldingRowDto> Holdings);

/// <summary>
/// The state of the group's issues, or holdings, over the period, whoever changed them: the
/// Backlog tab and the open figures on the Overview. <see cref="States"/> is the stacking order.
/// </summary>
public sealed record MiBacklogDto(
    string GroupId,
    MiStatusDto Status,
    MiPeriodDto Period,
    string Unit,
    string? RuleId,
    IReadOnlyList<MiBucketDto> Buckets,
    IReadOnlyList<string> States,
    IReadOnlyList<MiStatePointDto> ByStatus,
    IReadOnlyList<MiActivityPointDto> ActiveInactive,
    IReadOnlyList<int> OpenOverTime,
    int OpenAtStart,
    int OpenAtEnd,
    MiWaterfallDto Waterfall,
    int MinimumIssueAgeDays,
    IReadOnlyList<MiAgeBandDto> Age,
    IReadOnlyList<MiRuleOpenDto> ByRule,
    IReadOnlyList<MiHoldingRowDto> OldestOpenHoldings,
    MiStalledDto Stalled,
    int UnassignedOpenHoldings,
    IReadOnlyList<MiMarkerDto> Markers);

/// <summary>
/// What a refresh did: whether it rebuilt the figures, and how current they are now. When it
/// rebuilt them, <see cref="MismatchedIssueIds"/> names the issues counted in the mismatches.
/// </summary>
public sealed record MiRefreshResultDto(bool Rebuilt, MiStatusDto Status, IReadOnlyList<string>? MismatchedIssueIds = null);
