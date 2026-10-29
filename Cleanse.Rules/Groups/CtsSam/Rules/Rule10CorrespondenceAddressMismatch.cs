using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam;

namespace Cleanse.Rules.Groups.CtsSam.Rules;

/// <summary>
/// RULE 10, PRIORITY 4. The address SAM writes to for the CPH holder is not the address CTS
/// writes to for the same location.
/// </summary>
/// <remarks>
/// Subordinate to rule 2 like rules 1 and 3: it requires the CPH to exist on both sides.
/// <para>
/// The comparison is between <em>correspondence</em> addresses, not the location's. On the SAM
/// side that is the contact address of every party holding the CPH (<c>holder</c>), falling back
/// to the keeper and owner parties for the handful of holdings with no holder recorded. On the
/// CTS side it is the correspondence group when CTS has one and the location group otherwise,
/// which is where CTS itself sends mail. The keeper group is the location group in nineteen
/// records out of twenty and the contact group is rare, so neither is a comparison target; both
/// are consulted only to say whether CTS knows SAM's postcode in some other slot.
/// </para>
/// <para>
/// Only the postcode is compared, literally (<see cref="Postcodes"/>). An issue is raised when
/// no SAM party's postcode equals the CTS postcode: if one holder's address agrees with CTS, the
/// two systems agree on where correspondence goes. When either side has no postcode there is
/// nothing to compare and the rule is silent. Comparing the premises within a shared postcode is
/// deferred: it needs the equivalence vocabulary rule 3 has, and its own baseline.
/// </para>
/// <para>
/// The finding carries both addresses, which CTS group was compared, whether SAM's postcode
/// appears in another CTS group (a CTS correspondence override, or a CTS-internal typo), and
/// whether SAM's correspondence address is the holding's own, which separates a postcode wrong in
/// one system from a keeper who corresponds from somewhere else.
/// </para>
/// </remarks>
public sealed class Rule10CorrespondenceAddressMismatch : ICleanseRule
{
    public static readonly RuleDescriptor Rule = new(
        ruleId: "10",
        ruleNumber: "10",
        name: "Correspondence address mismatch",
        errorCode: "10",
        errorDescription: "Inconsistent-correspondence-addresses-in-CTS-and-Sam",
        summary: "The postcode of the SAM CPH holder's correspondence address differs from the postcode CTS sends correspondence to: its correspondence address, or the location address when it has none.");

    public RuleDescriptor Descriptor => Rule;

    public int Priority => 4;

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

            values["samCorrespondenceAddress"] = Join(assessment.SamParties.Select(Format));
            values["samCorrespondencePostcode"] = Join(assessment.SamParties.Select(p => p.Postcode!.Trim()));
            values["samCorrespondencePartyId"] = Join(assessment.SamParties.Select(p => p.SourcePartyId));
            values["samCorrespondencePartyRole"] = string.Join(",", assessment.SamParties.Select(p => p.Role).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

            if (assessment.SamParties.Count > 1)
            {
                values["samCorrespondencePartyCount"] = assessment.SamParties.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            if (Add(values, "samHoldingPostcode", record.SamHoldingPostcode))
            {
                values["samCorrespondenceIsHoldingAddress"] = Flag(assessment.SamParties.Any(p => Postcodes.AreSame(p.Postcode, record.SamHoldingPostcode)));
            }

            values["ctsCorrespondenceAddress"] = Format(assessment.CtsAddress!);
            values["ctsCorrespondencePostcode"] = assessment.CtsAddress!.PostCode!.Trim();
            values["ctsCorrespondenceSource"] = assessment.Source == CtsCorrespondenceSource.Correspondence ? "correspondence" : "location";
            values["samPostcodeKnownToCts"] = Flag(assessment.SamPostcodeKnownToCts);

            if (assessment.SamPostcodeKnownToCts)
            {
                values["ctsGroupsMatchingSam"] = string.Join(",", assessment.CtsGroupsMatchingSam);
            }

            values["addressMismatchReason"] = "postcode";
        });
    }

    /// <summary>
    /// The whole decision for one record, exposed so that it can be tested and reconciled against
    /// a snapshot without going through the engine.
    /// </summary>
    public static CorrespondenceAddressAssessment Assess(CtsSamRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var (ctsAddress, source) = EffectiveCtsAddress(record);
        var ctsPostcode = Postcodes.Normalise(ctsAddress?.PostCode);
        var samParties = SamCorrespondenceParties(record)
            .Where(party => Postcodes.Normalise(party.Postcode) is not null)
            .ToList();

        if (ctsPostcode is null || samParties.Count == 0)
        {
            return new CorrespondenceAddressAssessment(CorrespondenceAddressOutcome.NothingToCompare, source, ctsAddress, samParties, []);
        }

        if (samParties.Any(party => Postcodes.AreSame(party.Postcode, ctsPostcode)))
        {
            return new CorrespondenceAddressAssessment(CorrespondenceAddressOutcome.Equivalent, source, ctsAddress, samParties, []);
        }

        var samPostcodes = samParties.Select(party => Postcodes.Normalise(party.Postcode)!).ToHashSet(StringComparer.Ordinal);
        var matchingGroups = new List<string>();
        foreach (var (group, postcode) in OtherCtsPostcodes(record, source))
        {
            var normalised = Postcodes.Normalise(postcode);
            if (normalised is not null && samPostcodes.Contains(normalised))
            {
                matchingGroups.Add(group);
            }
        }

        return new CorrespondenceAddressAssessment(CorrespondenceAddressOutcome.PostcodeDifferent, source, ctsAddress, samParties, matchingGroups);
    }

    /// <summary>The CTS address mail actually goes to: the correspondence group when CTS has one, else the location.</summary>
    public static (CtsAddress? Address, CtsCorrespondenceSource Source) EffectiveCtsAddress(CtsSamRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record.CtsCorrespondenceAddress is { IsEmpty: false } correspondence
            ? (correspondence, CtsCorrespondenceSource.Correspondence)
            : (record.CtsLocationAddress, CtsCorrespondenceSource.Location);
    }

    /// <summary>
    /// The SAM parties whose correspondence address is compared: the CPH holders, one entry per
    /// party, or the keepers and owners when SAM records no holder.
    /// </summary>
    public static IReadOnlyList<SamPartyAddress> SamCorrespondenceParties(CtsSamRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var holders = Distinct(record.SamPartyAddresses.Where(party => IsRole(party, SamPartyAddress.HolderRole)));
        if (holders.Count > 0)
        {
            return holders;
        }

        return Distinct(record.SamPartyAddresses.Where(party =>
            IsRole(party, SamPartyAddress.KeeperRole) || IsRole(party, SamPartyAddress.OwnerRole)));
    }

    private static IEnumerable<(string Group, string? Postcode)> OtherCtsPostcodes(CtsSamRecord record, CtsCorrespondenceSource compared)
    {
        if (compared != CtsCorrespondenceSource.Correspondence)
        {
            yield return ("correspondence", record.CtsCorrespondenceAddress?.PostCode);
        }

        if (compared != CtsCorrespondenceSource.Location)
        {
            yield return ("location", record.CtsLocationAddress?.PostCode);
        }

        yield return ("keeper", record.CtsKeeperPostCode);
        yield return ("contact", record.CtsContactPostCode);
    }

    private static List<SamPartyAddress> Distinct(IEnumerable<SamPartyAddress> parties) =>
        [.. parties
            .GroupBy(party => party.SourcePartyId, StringComparer.Ordinal)
            .Select(group => group.First())
            .OrderBy(party => party.SourcePartyId, StringComparer.Ordinal)];

    private static bool IsRole(SamPartyAddress party, string role) =>
        string.Equals(party.Role, role, StringComparison.OrdinalIgnoreCase);

    private static string Format(SamPartyAddress party) =>
        string.Join(", ", party.Lines.Append(party.Postcode!.Trim()));

    private static string Format(CtsAddress address) =>
        string.Join(", ", address.Lines.Append(address.PostCode!.Trim()));

    /// <summary>Several parties are shown side by side; a slash cannot occur inside an address line.</summary>
    private static string Join(IEnumerable<string> values) => string.Join(" / ", values);

    private static string Flag(bool value) => value ? "true" : "false";

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

public enum CorrespondenceAddressOutcome
{
    /// <summary>SAM has no party postcode, or CTS no postcode, to compare.</summary>
    NothingToCompare,

    /// <summary>A SAM party's postcode is the CTS postcode.</summary>
    Equivalent,

    /// <summary>No SAM party's postcode is the CTS postcode. An issue.</summary>
    PostcodeDifferent
}

/// <summary>Which CTS address group stood as the correspondence address.</summary>
public enum CtsCorrespondenceSource
{
    Correspondence,
    Location
}

/// <param name="Source">The CTS group compared.</param>
/// <param name="CtsAddress">The CTS address compared; null only when CTS has no location group at all.</param>
/// <param name="SamParties">The SAM parties compared, each with a postcode, in party-id order.</param>
/// <param name="CtsGroupsMatchingSam">The other CTS groups whose postcode is one of SAM's; empty unless the outcome is an issue.</param>
public sealed record CorrespondenceAddressAssessment(
    CorrespondenceAddressOutcome Outcome,
    CtsCorrespondenceSource Source,
    CtsAddress? CtsAddress,
    IReadOnlyList<SamPartyAddress> SamParties,
    IReadOnlyList<string> CtsGroupsMatchingSam)
{
    public bool IsIssue => Outcome == CorrespondenceAddressOutcome.PostcodeDifferent;

    /// <summary>Whether CTS holds SAM's postcode in a group other than the one compared.</summary>
    public bool SamPostcodeKnownToCts => CtsGroupsMatchingSam.Count > 0;
}
