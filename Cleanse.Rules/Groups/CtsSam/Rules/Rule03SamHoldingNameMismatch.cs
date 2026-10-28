using System.Globalization;
using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam;

namespace Cleanse.Rules.Groups.CtsSam.Rules;

/// <summary>
/// RULE 3, PRIORITY 3. The SAM holding's name is missing, a placeholder, or does not identify
/// the same place as the CTS open location's name.
/// </summary>
/// <remarks>
/// Subordinate to rule 2 in the same way as rule 1: it requires the CPH to exist on both sides.
/// SAM is the canonical system and CTS is being retired, so the aim is to have SAM carry a name a
/// person could recognise the location by while CTS is still there to check against.
/// <para>
/// The SAM name is compared with the CTS name line and then with address line 2, which is where
/// CTS files the holding name when the name line is a trading name, a care-of line or a building.
/// What "the same place" means is defined in <see cref="HoldingNameEquivalence"/>. When neither
/// line is equivalent but the CTS name line is not a place name at all — a numbered postal
/// address, a trading line, or the holding's own street or village — the rule stays silent,
/// because nothing in SAM could be corrected against it.
/// </para>
/// <para>
/// The finding carries SAM's address building name and whether <em>it</em> is equivalent to the
/// CTS name. When it is, the holding name is a placeholder, a business or a typo while SAM's own
/// address already holds the right name, and the fix is to copy it across.
/// </para>
/// </remarks>
public sealed class Rule03SamHoldingNameMismatch : ICleanseRule
{
    public static readonly RuleDescriptor Rule = new(
        ruleId: "3",
        ruleNumber: "3",
        name: "Holding name mismatch",
        errorCode: "03",
        errorDescription: "Holding-name-mismatch-in-Sam",
        summary: "The SAM Holding has no usable FeatureName, or it does not identify the same place as the CTS open location name.");

    public RuleDescriptor Descriptor => Rule;

    public int Priority => 3;

    public RuleStatus Status => RuleStatus.Active;

    public bool AppliesTo(CtsSamRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        return context.ExistsInSam;
    }

    public RuleFinding? Evaluate(CtsSamRuleContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var record = context.Record;
        var assessment = Assess(record);
        if (!assessment.IsIssue)
        {
            return null;
        }

        return context.Issue(Descriptor, values =>
        {
            values["samHoldingId"] = record.SamHoldingId!;
            Add(values, "samFeatureName", record.SamFeatureName);

            if (Add(values, "samAddressName", record.SamAddressName))
            {
                values["samAddressNameMatchesCts"] = AddressNameMatchesCts(record) ? "true" : "false";
            }

            Add(values, "ctsLocationName", record.CtsLocationName);
            Add(values, "ctsLocationAddress2", record.CtsLocationAddress2);

            values["nameMismatchReason"] = assessment.Outcome switch
            {
                HoldingNameOutcome.Missing => "missing",
                HoldingNameOutcome.Placeholder => "placeholder",
                _ => "different"
            };

            if (assessment.Outcome == HoldingNameOutcome.Different)
            {
                values["nameSimilarity"] = assessment.Similarity.ToString("0.00", CultureInfo.InvariantCulture);
            }
        });
    }

    /// <summary>
    /// The whole decision for one record, exposed so that it can be tested and reconciled against
    /// a snapshot without going through the engine.
    /// </summary>
    public static HoldingNameAssessment Assess(CtsSamRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (string.IsNullOrWhiteSpace(record.SamFeatureName))
        {
            return new HoldingNameAssessment(HoldingNameOutcome.Missing, NameMatch.Different, 0d);
        }

        if (HoldingNameEquivalence.IsPlaceholder(record.SamFeatureName))
        {
            return new HoldingNameAssessment(HoldingNameOutcome.Placeholder, NameMatch.Different, 0d);
        }

        var sam = HoldingNameEquivalence.Canonicalise(record.SamFeatureName);
        var compared = false;
        var bestSimilarity = 0d;

        foreach (var (candidate, allowContainment) in Candidates(record))
        {
            var cts = HoldingNameEquivalence.Canonicalise(candidate);
            if (cts.Count == 0)
            {
                continue;
            }

            var comparison = HoldingNameEquivalence.Compare(sam, cts, allowContainment);
            if (comparison.IsEquivalent)
            {
                return new HoldingNameAssessment(HoldingNameOutcome.Equivalent, comparison.Match, comparison.Similarity);
            }

            compared = true;
            bestSimilarity = Math.Max(bestSimilarity, comparison.Similarity);
        }

        if (!compared || !HoldingNameEquivalence.IsPlaceName(record.CtsLocationName, record.SamStreet, record.SamTown, record.SamLocality))
        {
            return new HoldingNameAssessment(HoldingNameOutcome.NothingToCompare, NameMatch.Different, bestSimilarity);
        }

        return new HoldingNameAssessment(HoldingNameOutcome.Different, NameMatch.Different, bestSimilarity);
    }

    /// <summary>
    /// Whether SAM's address building name would have satisfied the rule in place of the holding
    /// name, in which case the correction is already sitting in SAM's own address.
    /// </summary>
    public static bool AddressNameMatchesCts(CtsSamRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return !string.IsNullOrWhiteSpace(record.SamAddressName)
            && Assess(record with { SamFeatureName = record.SamAddressName }).Outcome == HoldingNameOutcome.Equivalent;
    }

    /// <summary>The CTS lines to try, in order. Containment is only trusted against the name line.</summary>
    private static IEnumerable<(string? Candidate, bool AllowContainment)> Candidates(CtsSamRecord record)
    {
        yield return (record.CtsLocationName, true);
        yield return (record.CtsLocationAddress2, false);
    }

    private static bool Add(IDictionary<string, string> values, string key, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        values[key] = value.Trim();
        return true;
    }
}

public enum HoldingNameOutcome
{
    /// <summary>SAM records no name. An issue.</summary>
    Missing,

    /// <summary>SAM's name is a placeholder, or describes a relationship to a place without naming it. An issue.</summary>
    Placeholder,

    /// <summary>A CTS line identifies the same place.</summary>
    Equivalent,

    /// <summary>No CTS line is equivalent, but the CTS name is not a place name, so there is nothing to correct against.</summary>
    NothingToCompare,

    /// <summary>The names identify different places. An issue.</summary>
    Different
}

/// <param name="Match">How the names were found equivalent; <see cref="NameMatch.Different"/> otherwise.</param>
/// <param name="Similarity">The best similarity seen across the CTS lines compared.</param>
public sealed record HoldingNameAssessment(HoldingNameOutcome Outcome, NameMatch Match, double Similarity)
{
    public bool IsIssue => Outcome is HoldingNameOutcome.Missing or HoldingNameOutcome.Placeholder or HoldingNameOutcome.Different;
}
