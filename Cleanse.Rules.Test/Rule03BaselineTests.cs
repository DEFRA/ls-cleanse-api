using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam.Rules;
using Cleanse.Rules.Krds;
using Cleanse.Rules.Krds.Snapshots;

namespace Cleanse.Rules.Test;

/// <summary>
/// Reconciles rule 3 against the snapshot its definition was baselined on. Skipped where the
/// snapshot is not present. The expected figures are the design baseline: a change here is a
/// change to what the business agreed, not a refactoring.
/// </summary>
public class Rule03BaselineTests
{
    private const string SnapshotPath = @"A:\tmp\defra-krds-duckdb\krds-db_20261026070002.sqlite";

    private static bool SnapshotAvailable => File.Exists(SnapshotPath);

    [Fact]
    public async Task Reproduces_the_baseline_of_26_october_2026()
    {
        Assert.SkipUnless(SnapshotAvailable, $"No KRDS snapshot at {SnapshotPath}.");

        var outcomes = new Dictionary<HoldingNameOutcome, int>();
        var matches = new Dictionary<NameMatch, int>();
        var quickFixes = 0;
        var pairs = 0;

        await using var source = new KrdsSqliteDataSource(new KrdsSnapshot(SnapshotPath, ownsFile: false, "baseline"));

        await foreach (var record in source.StreamRecordsAsync(TestContext.Current.CancellationToken))
        {
            if (record.SamHoldingId is null)
            {
                continue;
            }

            pairs++;
            var assessment = Rule03SamHoldingNameMismatch.Assess(record);
            outcomes[assessment.Outcome] = outcomes.GetValueOrDefault(assessment.Outcome) + 1;

            if (assessment.Outcome == HoldingNameOutcome.Equivalent)
            {
                matches[assessment.Match] = matches.GetValueOrDefault(assessment.Match) + 1;
            }
            else if (assessment.IsIssue && Rule03SamHoldingNameMismatch.AddressNameMatchesCts(record))
            {
                quickFixes++;
            }
        }

        Assert.Equal(40_473, pairs);

        Assert.Equal(0, outcomes.GetValueOrDefault(HoldingNameOutcome.Missing));
        Assert.Equal(338, outcomes.GetValueOrDefault(HoldingNameOutcome.Placeholder));
        Assert.Equal(3_674, outcomes.GetValueOrDefault(HoldingNameOutcome.Different));
        Assert.Equal(869, outcomes.GetValueOrDefault(HoldingNameOutcome.NothingToCompare));
        Assert.Equal(35_592, outcomes.GetValueOrDefault(HoldingNameOutcome.Equivalent));

        Assert.Equal(30_539, matches.GetValueOrDefault(NameMatch.Canonical));
        Assert.Equal(1_901, matches.GetValueOrDefault(NameMatch.Generic));
        Assert.Equal(2_172, matches.GetValueOrDefault(NameMatch.Contains));
        Assert.Equal(980, matches.GetValueOrDefault(NameMatch.Spelling));

        Assert.Equal(746, quickFixes);
    }
}
