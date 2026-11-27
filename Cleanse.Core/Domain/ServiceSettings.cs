namespace Cleanse.Core.Domain;

/// <summary>
/// The settings an admin changes for the whole service, rather than for one group. Who changed
/// them and when is in the audit log.
/// </summary>
public sealed record ServiceSettings(int MinimumIssueAgeDays)
{
    public const int DefaultMinimumIssueAgeDays = 14;

    public const int MaxMinimumIssueAgeDays = 365;

    /// <summary>How an audit entry names the minimum issue age.</summary>
    public const string MinimumIssueAgeDaysName = "minimum-issue-age-days";

    /// <summary>What applies until an admin first saves.</summary>
    public static ServiceSettings Default { get; } = new(DefaultMinimumIssueAgeDays);

    public static Result<ServiceSettings> Create(int minimumIssueAgeDays) =>
        minimumIssueAgeDays is < 0 or > MaxMinimumIssueAgeDays
            ? DomainErrors.Validation(
                $"The minimum issue age must be a whole number of days from 0 to {MaxMinimumIssueAgeDays}.")
            : new ServiceSettings(minimumIssueAgeDays);
}
