using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam.Rules;
using Cleanse.Rules.Test.Support;

namespace Cleanse.Rules.Test;

public class Rule10Tests
{
    private readonly Rule10CorrespondenceAddressMismatch _rule = new();

    [Fact]
    public void Requires_the_holding_to_exist_in_sam()
    {
        Assert.False(_rule.AppliesTo(Records.Context(Records.CtsOnly())));
        Assert.True(_rule.AppliesTo(Records.Context(Records.InBoth())));
    }

    [Fact]
    public void Describes_itself_as_rule_10()
    {
        Assert.Equal("10", _rule.Descriptor.RuleId);
        Assert.Equal("10", _rule.Descriptor.RuleNumber);
        Assert.Equal("10", _rule.Descriptor.ErrorCode);
        Assert.Equal("Inconsistent-correspondence-addresses-in-CTS-and-Sam", _rule.Descriptor.ErrorDescription);
        Assert.Equal(4, _rule.Priority);
        Assert.Equal(RuleStatus.Active, _rule.Status);
    }

    [Theory]
    [InlineData("MK45 5WF")]
    [InlineData("mk45 5wf")]
    [InlineData("MK455WF")]
    [InlineData("  MK45  5WF ")]
    [InlineData("'MK45 5WF'")]
    public void Is_silent_when_the_holder_writes_from_the_postcode_cts_has(string samPostcode)
    {
        var record = Records.InBoth() with { SamPartyAddresses = [Records.Holder("C100", samPostcode)] };

        Assert.Null(_rule.Evaluate(Records.Context(record)));
        Assert.Equal(CorrespondenceAddressOutcome.Equivalent, Rule10CorrespondenceAddressMismatch.Assess(record).Outcome);
    }

    [Fact]
    public void Fires_when_no_holder_writes_from_the_postcode_cts_has()
    {
        var record = Records.InBoth() with { SamPartyAddresses = [Records.Holder("C100", "MK45 5WG")] };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.NotNull(finding);
        Assert.Equal("10", finding.Descriptor.RuleId);
        Assert.Equal(record.Cph, finding.Cph);
        Assert.Equal(record.SamHoldingId, finding.Context["samHoldingId"]);
        Assert.Equal("Wood Farm", finding.Context["samFeatureName"]);
        Assert.Equal("Wood Farm, Bedford Road, Clophill, BEDFORD, MK45 5WG", finding.Context["samCorrespondenceAddress"]);
        Assert.Equal("MK45 5WG", finding.Context["samCorrespondencePostcode"]);
        Assert.Equal("C100", finding.Context["samCorrespondencePartyId"]);
        Assert.Equal("holder", finding.Context["samCorrespondencePartyRole"]);
        Assert.False(finding.Context.ContainsKey("samCorrespondencePartyCount"));
        Assert.Equal("MK45 5WF", finding.Context["samHoldingPostcode"]);
        Assert.Equal("false", finding.Context["samCorrespondenceIsHoldingAddress"]);
        Assert.Equal("CLOPHILL, BEDFORD, MK45 5WF", finding.Context["ctsCorrespondenceAddress"]);
        Assert.Equal("MK45 5WF", finding.Context["ctsCorrespondencePostcode"]);
        Assert.Equal("location", finding.Context["ctsCorrespondenceSource"]);
        Assert.Equal("false", finding.Context["samPostcodeKnownToCts"]);
        Assert.False(finding.Context.ContainsKey("ctsGroupsMatchingSam"));
        Assert.Equal("postcode", finding.Context["addressMismatchReason"]);
        Assert.Equal("MK45 5WF", finding.Context["ctsKeeperPostCode"]);
    }

    [Fact]
    public void Compares_postcodes_literally()
    {
        // A letter O for a zero in CTS is a different postcode, and that is the finding.
        var record = Records.InBoth() with
        {
            CtsLocationAddress = new CtsAddress("POND HOUSE FARM", "BURGHFIELD", "READING", "BERKS", null, "RG3O 3SE"),
            CtsKeeperPostCode = "RG3O 3SE",
            SamHoldingPostcode = "RG30 3SE",
            SamPartyAddresses = [Records.Holder("C100", "RG30 3SE")]
        };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.NotNull(finding);
        Assert.Equal("true", finding.Context["samCorrespondenceIsHoldingAddress"]);
    }

    [Fact]
    public void Compares_the_correspondence_group_when_cts_has_one()
    {
        // The holder writes from the farm, which is CTS's location; CTS sends mail elsewhere.
        var record = Records.InBoth() with
        {
            CtsCorrespondenceAddress = new CtsAddress("1 STRAWBERRY CLOSE", "PRESTWOOD", "GREAT MISSENDEN", "BUCKS", null, "HP16 0SJ")
        };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.NotNull(finding);
        Assert.Equal("correspondence", finding.Context["ctsCorrespondenceSource"]);
        Assert.Equal("1 STRAWBERRY CLOSE, PRESTWOOD, GREAT MISSENDEN, BUCKS, HP16 0SJ", finding.Context["ctsCorrespondenceAddress"]);
        Assert.Equal("HP16 0SJ", finding.Context["ctsCorrespondencePostcode"]);
        Assert.Equal("true", finding.Context["samPostcodeKnownToCts"]);
        Assert.Equal("location,keeper", finding.Context["ctsGroupsMatchingSam"]);
    }

    [Fact]
    public void Treats_an_empty_correspondence_group_as_absent()
    {
        var record = Records.InBoth() with { CtsCorrespondenceAddress = new CtsAddress(null, null, null, null, null, null) };

        Assert.Null(_rule.Evaluate(Records.Context(record)));
        Assert.Equal(CtsCorrespondenceSource.Location, Rule10CorrespondenceAddressMismatch.EffectiveCtsAddress(record).Source);
    }

    [Fact]
    public void Uses_a_correspondence_group_that_has_only_a_postcode()
    {
        var record = Records.InBoth() with { CtsCorrespondenceAddress = new CtsAddress(null, null, null, null, null, "HP16 0SJ") };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.NotNull(finding);
        Assert.Equal("HP16 0SJ", finding.Context["ctsCorrespondenceAddress"]);
    }

    [Fact]
    public void Is_silent_when_any_of_several_holders_matches()
    {
        var record = Records.InBoth() with
        {
            SamPartyAddresses = [Records.Holder("C200", "LS1 1AA"), Records.Holder("C100", "MK45 5WF")]
        };

        Assert.Null(_rule.Evaluate(Records.Context(record)));
    }

    [Fact]
    public void Reports_every_holder_when_none_matches()
    {
        var record = Records.InBoth() with
        {
            SamPartyAddresses =
            [
                Records.Holder("C200", "PR6 8SD", "Boardman Heights Farm"),
                Records.Holder("C100", "L40 8JQ", "Scarisbrick House Farm")
            ]
        };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.NotNull(finding);
        Assert.Equal(
            "Scarisbrick House Farm, Bedford Road, Clophill, BEDFORD, L40 8JQ / Boardman Heights Farm, Bedford Road, Clophill, BEDFORD, PR6 8SD",
            finding.Context["samCorrespondenceAddress"]);
        Assert.Equal("L40 8JQ / PR6 8SD", finding.Context["samCorrespondencePostcode"]);
        Assert.Equal("C100 / C200", finding.Context["samCorrespondencePartyId"]);
        Assert.Equal("2", finding.Context["samCorrespondencePartyCount"]);
    }

    [Fact]
    public void Lists_a_party_once_however_many_roles_it_holds()
    {
        var record = Records.InBoth() with
        {
            SamPartyAddresses =
            [
                Records.Holder("C100", "MK45 5WG"),
                Records.Party("C100", SamPartyAddress.OwnerRole, "MK45 5WG"),
                Records.Party("C100", SamPartyAddress.KeeperRole, "MK45 5WG")
            ]
        };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.NotNull(finding);
        Assert.Equal("C100", finding.Context["samCorrespondencePartyId"]);
        Assert.False(finding.Context.ContainsKey("samCorrespondencePartyCount"));
    }

    [Fact]
    public void Compares_only_the_holders_when_sam_records_one()
    {
        var holderMatches = Records.InBoth() with
        {
            SamPartyAddresses = [Records.Holder("C100", "MK45 5WF"), Records.Party("C300", SamPartyAddress.KeeperRole, "LS1 1AA")]
        };
        var keeperMatches = Records.InBoth() with
        {
            SamPartyAddresses = [Records.Holder("C100", "LS1 1AA"), Records.Party("C300", SamPartyAddress.KeeperRole, "MK45 5WF")]
        };

        Assert.Null(_rule.Evaluate(Records.Context(holderMatches)));

        var finding = _rule.Evaluate(Records.Context(keeperMatches));
        Assert.NotNull(finding);
        Assert.Equal("C100", finding.Context["samCorrespondencePartyId"]);
    }

    [Fact]
    public void Falls_back_to_keepers_and_owners_when_sam_records_no_holder()
    {
        var record = Records.InBoth() with
        {
            SamPartyAddresses =
            [
                Records.Party("C300", SamPartyAddress.KeeperRole, "LS1 1AA"),
                Records.Party("C400", SamPartyAddress.OwnerRole, "LS1 1AB")
            ]
        };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.NotNull(finding);
        Assert.Equal("C300 / C400", finding.Context["samCorrespondencePartyId"]);
        Assert.Equal("keeper,owner", finding.Context["samCorrespondencePartyRole"]);

        var matching = record with { SamPartyAddresses = [Records.Party("C300", SamPartyAddress.KeeperRole, "MK45 5WF")] };
        Assert.Null(_rule.Evaluate(Records.Context(matching)));
    }

    [Fact]
    public void Is_silent_when_there_is_nothing_to_compare()
    {
        var noParties = Records.InBoth() with { SamPartyAddresses = [] };
        var noSamPostcode = Records.InBoth() with { SamPartyAddresses = [Records.Holder("C100", null), Records.Holder("C200", "  ")] };
        var noCtsPostcode = Records.InBoth() with { CtsLocationAddress = new CtsAddress("WOOD FARM", "CLOPHILL", null, null, null, null) };
        var noCtsGroups = Records.InBoth() with { CtsLocationAddress = null };

        foreach (var record in new[] { noParties, noSamPostcode, noCtsPostcode, noCtsGroups })
        {
            Assert.Null(_rule.Evaluate(Records.Context(record)));
            Assert.Equal(CorrespondenceAddressOutcome.NothingToCompare, Rule10CorrespondenceAddressMismatch.Assess(record).Outcome);
        }
    }

    [Fact]
    public void Ignores_parties_without_a_postcode_when_others_have_one()
    {
        var record = Records.InBoth() with
        {
            SamPartyAddresses = [Records.Holder("C100", null), Records.Holder("C200", "LS1 1AA")]
        };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.NotNull(finding);
        Assert.Equal("C200", finding.Context["samCorrespondencePartyId"]);
    }

    [Fact]
    public void Says_where_else_cts_knows_the_sam_postcode()
    {
        var inContact = Records.InBoth() with
        {
            SamPartyAddresses = [Records.Holder("C100", "YO19 5GF")],
            CtsContactPostCode = "YO19 5GF"
        };
        var inKeeper = Records.InBoth() with
        {
            SamPartyAddresses = [Records.Holder("C100", "HP22 6PQ")],
            CtsKeeperPostCode = "HP22 6PQ"
        };

        var contact = _rule.Evaluate(Records.Context(inContact));
        var keeper = _rule.Evaluate(Records.Context(inKeeper));

        Assert.NotNull(contact);
        Assert.Equal("true", contact.Context["samPostcodeKnownToCts"]);
        Assert.Equal("contact", contact.Context["ctsGroupsMatchingSam"]);

        Assert.NotNull(keeper);
        Assert.Equal("keeper", keeper.Context["ctsGroupsMatchingSam"]);
    }

    [Fact]
    public void Omits_the_holding_postcode_comparison_when_sam_has_none()
    {
        var record = Records.InBoth() with
        {
            SamHoldingPostcode = null,
            SamPartyAddresses = [Records.Holder("C100", "LS1 1AA")]
        };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.NotNull(finding);
        Assert.False(finding.Context.ContainsKey("samHoldingPostcode"));
        Assert.False(finding.Context.ContainsKey("samCorrespondenceIsHoldingAddress"));
    }

    [Fact]
    public void Exposes_the_parties_and_groups_it_compared()
    {
        var record = Records.InBoth() with
        {
            SamPartyAddresses = [Records.Holder("C200", "LS1 1AA"), Records.Holder("C100", "LS1 1AB")],
            CtsCorrespondenceAddress = new CtsAddress("THE ESTATE OFFICE", "QUEEN STREET", "WADDESDON", null, null, "HP18 0NH"),
            CtsKeeperPostCode = "LS1 1AB"
        };

        var assessment = Rule10CorrespondenceAddressMismatch.Assess(record);

        Assert.Equal(CorrespondenceAddressOutcome.PostcodeDifferent, assessment.Outcome);
        Assert.True(assessment.IsIssue);
        Assert.Equal(CtsCorrespondenceSource.Correspondence, assessment.Source);
        Assert.Equal("HP18 0NH", assessment.CtsAddress!.PostCode);
        Assert.Equal(["C100", "C200"], assessment.SamParties.Select(p => p.SourcePartyId));
        Assert.Equal(["keeper"], assessment.CtsGroupsMatchingSam);
    }
}
