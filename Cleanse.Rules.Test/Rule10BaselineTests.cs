using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam.Rules;
using Cleanse.Rules.Krds;
using Cleanse.Rules.Krds.Snapshots;

namespace Cleanse.Rules.Test;

/// <summary>
/// Reconciles rule 10 against the snapshot its definition was baselined on. Skipped where the
/// snapshot is not present, which includes every CI run. The expected figures are the design
/// baseline: a change here is a change to what the business agreed, not a refactoring.
/// </summary>
public class Rule10BaselineTests
{
    private const string SnapshotPath = @"A:\tmp\defra-krds-duckdb\krds-db_20261026070002.sqlite";

    private static bool SnapshotAvailable => File.Exists(SnapshotPath);

    [Fact]
    public async Task Reproduces_the_baseline_of_26_october_2026()
    {
        Assert.SkipUnless(SnapshotAvailable, $"No KRDS snapshot at {SnapshotPath}.");

        var outcomes = new Dictionary<CorrespondenceAddressOutcome, int>();
        var sources = new Dictionary<CtsCorrespondenceSource, int>();
        var knownElsewhere = 0;
        var fromKeepersAndOwners = 0;
        var severalParties = 0;
        var pairs = 0;

        await using var source = new KrdsSqliteDataSource(new KrdsSnapshot(SnapshotPath, ownsFile: false, "baseline"));

        await foreach (var record in source.StreamRecordsAsync(TestContext.Current.CancellationToken))
        {
            if (record.SamHoldingId is null)
            {
                continue;
            }

            pairs++;
            var assessment = Rule10CorrespondenceAddressMismatch.Assess(record);
            outcomes[assessment.Outcome] = outcomes.GetValueOrDefault(assessment.Outcome) + 1;

            if (!assessment.IsIssue)
            {
                continue;
            }

            sources[assessment.Source] = sources.GetValueOrDefault(assessment.Source) + 1;

            if (assessment.SamPostcodeKnownToCts)
            {
                knownElsewhere++;
            }

            if (assessment.SamParties.All(party => party.Role != SamPartyAddress.HolderRole))
            {
                fromKeepersAndOwners++;
            }

            if (assessment.SamParties.Count > 1)
            {
                severalParties++;
            }
        }

        Assert.Equal(40_473, pairs);

        Assert.Equal(2_912, outcomes.GetValueOrDefault(CorrespondenceAddressOutcome.PostcodeDifferent));
        Assert.Equal(27, outcomes.GetValueOrDefault(CorrespondenceAddressOutcome.NothingToCompare));
        Assert.Equal(37_534, outcomes.GetValueOrDefault(CorrespondenceAddressOutcome.Equivalent));

        Assert.Equal(1_472, sources.GetValueOrDefault(CtsCorrespondenceSource.Location));
        Assert.Equal(1_440, sources.GetValueOrDefault(CtsCorrespondenceSource.Correspondence));
        Assert.Equal(644, knownElsewhere);

        // Reported so that a change in either is noticed; neither figure was agreed with the business.
        Assert.InRange(fromKeepersAndOwners, 0, 31);
        Assert.InRange(severalParties, 0, 24);
    }
}
