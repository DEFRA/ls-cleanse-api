using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Imports;

namespace Cleanse.Core.Test.Support;

internal static class TestData
{
    public const string Group = "cts-sam";
    public const string Rule2 = "rule2";
    public const string Rule5 = "rule5";

    public static readonly DateTimeOffset Night1 = new(2026, 1, 10, 2, 0, 0, TimeSpan.Zero);

    public static readonly Actor Alice = Actor.Human("alice", "Alice Brown");

    public static IssueId Key(string cph, string ruleId = Rule2) => IssueId.Derive(ruleId, cph);

    public static IssueDetection Detection(string cph, string ruleId = Rule2) =>
        new(Key(cph, ruleId), ruleId, new Dictionary<string, string> { ["cph"] = cph });

    public static IssueDetection Detection(string cph, string ruleId, params (string Key, string Value)[] context)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal) { ["cph"] = cph };
        foreach (var (key, value) in context)
        {
            values[key] = value;
        }

        return new IssueDetection(Key(cph, ruleId), ruleId, values);
    }

    public static Issue NewIssue(
        string cph,
        string runId = "run-1",
        string groupId = Group,
        string ruleId = Rule2,
        DateTimeOffset? at = null) =>
        Issue.FromDetection(Detection(cph, ruleId), groupId, runId, at ?? Night1);

    /// <summary>
    /// A pending job from a file upload, before datasets. It has no dataset, so a store needs
    /// nothing else to hold it.
    /// </summary>
    public static ImportJob NewUploadJob(string groupId = Group, DateTimeOffset? at = null, int totalRows = 10) =>
        ImportJob.FromSnapshot(new ImportJobSnapshot(
            Guid.NewGuid(),
            groupId,
            "extract.csv",
            $"imports/{groupId}/{Guid.NewGuid()}.csv",
            ImportJobStatus.Pending,
            ImportJobCounters.Empty with { TotalRows = totalRows },
            "alice",
            at ?? Night1,
            StartedAtUtc: null,
            FinishedAtUtc: null,
            HeartbeatAtUtc: null,
            FailureReason: null,
            Version: 1));
}
