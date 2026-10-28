using Cleanse.Core.Domain;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam.Rules;
using Cleanse.Rules.Test.Support;

namespace Cleanse.Rules.Test;

public class Rule03Tests
{
    private readonly Rule03SamHoldingNameMismatch _rule = new();

    [Fact]
    public void Requires_the_holding_to_exist_in_sam()
    {
        Assert.False(_rule.AppliesTo(Records.Context(Records.CtsOnly())));
        Assert.True(_rule.AppliesTo(Records.Context(Records.InBoth())));
    }

    [Fact]
    public void Describes_itself_as_rule_3()
    {
        Assert.Equal("3", _rule.Descriptor.RuleId);
        Assert.Equal("3", _rule.Descriptor.RuleNumber);
        Assert.Equal("03", _rule.Descriptor.ErrorCode);
        Assert.Equal("Holding-name-mismatch-in-Sam", _rule.Descriptor.ErrorDescription);
        Assert.Equal(3, _rule.Priority);
        Assert.Equal(RuleStatus.Active, _rule.Status);
    }

    [Theory]
    [InlineData("Wood Farm", "WOOD FARM")]
    [InlineData("Wood Farm", "WOOD FM")]
    [InlineData("Land At Wood Farm", "WOOD FARM")]
    [InlineData("Wood Farm", "WOOD")]
    [InlineData("Wood Farm Cottage", "WOOD FARM")]
    [InlineData("Wood Farm", "WOOD FARM BEDFORD")]
    [InlineData("Fox Covet Farm", "FOX COVERT FARM")]
    public void Is_silent_when_the_names_identify_the_same_place(string sam, string cts)
    {
        Assert.Null(_rule.Evaluate(Records.Context(Named(sam, cts))));
    }

    [Fact]
    public void Is_silent_when_cts_records_no_name_to_compare_against()
    {
        // The shared fixture has a SAM name and no CTS name at all.
        Assert.Null(_rule.Evaluate(Records.Context(Records.InBoth())));
        Assert.Null(_rule.Evaluate(Records.Context(Named("Wood Farm", "LAND AT"))));
    }

    [Theory]
    [InlineData("12 FORE STREET", null)]
    [InlineData("T/A J SMITH & SONS", "EVERSHOT")]
    [InlineData("C/O THE DIRECTOR", null)]
    public void Is_silent_when_the_cts_name_is_an_address_or_trading_line(string cts, string? line2)
    {
        Assert.Null(_rule.Evaluate(Records.Context(Named("Ladymead Farm", cts, line2))));
    }

    [Fact]
    public void Is_silent_when_the_cts_name_is_the_holdings_own_street_or_village()
    {
        var street = Named("Rockhouse Farm", "WOOD LANE") with { SamStreet = "Wood Lane" };
        var village = Named("Brick Kiln Copse Farm", "ROGATE") with { SamLocality = "Rogate", SamTown = "PETERSFIELD" };

        Assert.Null(_rule.Evaluate(Records.Context(street)));
        Assert.Null(_rule.Evaluate(Records.Context(village)));
    }

    [Fact]
    public void Accepts_the_second_cts_address_line_when_the_first_is_not_the_name()
    {
        Assert.Null(_rule.Evaluate(Records.Context(Named("Rushfield House", "T/A JR WEBB", "RUSHFIELD HOUSE"))));
        Assert.Null(_rule.Evaluate(Records.Context(Named("Hyning Home Farm", "THE BARN", "HYNING HOME FARM"))));
    }

    [Fact]
    public void Does_not_trust_containment_against_the_second_address_line()
    {
        // Line 2 is usually the village; mentioning it is not naming the holding.
        var finding = _rule.Evaluate(Records.Context(Named("Land @ Bintree & North Elmham", "TOWER FARM", "BINTREE")));

        Assert.NotNull(finding);
        Assert.Equal("different", finding.Context["nameMismatchReason"]);
    }

    [Fact]
    public void Fires_when_sam_has_no_name()
    {
        var finding = _rule.Evaluate(Records.Context(Named(null, "WOOD FARM")));

        Assert.NotNull(finding);
        Assert.Equal("missing", finding.Context["nameMismatchReason"]);
        Assert.False(finding.Context.ContainsKey("samFeatureName"));
        Assert.False(finding.Context.ContainsKey("nameSimilarity"));
    }

    [Theory]
    [InlineData("n/a")]
    [InlineData("Unknown")]
    [InlineData("Land At")]
    [InlineData("Fields @")]
    public void Fires_when_sam_has_a_placeholder_for_a_name(string sam)
    {
        var finding = _rule.Evaluate(Records.Context(Named(sam, "WOOD FARM")));

        Assert.NotNull(finding);
        Assert.Equal("placeholder", finding.Context["nameMismatchReason"]);
        Assert.Equal(sam, finding.Context["samFeatureName"]);
        Assert.False(finding.Context.ContainsKey("nameSimilarity"));
    }

    [Fact]
    public void Fires_when_the_names_identify_different_places()
    {
        var finding = _rule.Evaluate(Records.Context(Named("Manhiers Farm", "MANHEIRS FARM", "CREED")));

        Assert.NotNull(finding);
        Assert.Equal("3", finding.Descriptor.RuleId);
        Assert.Equal("different", finding.Context["nameMismatchReason"]);
        Assert.Equal("0.75", finding.Context["nameSimilarity"]);
        Assert.Equal("Manhiers Farm", finding.Context["samFeatureName"]);
        Assert.Equal("MANHEIRS FARM", finding.Context["ctsLocationName"]);
        Assert.Equal("CREED", finding.Context["ctsLocationAddress2"]);
        Assert.Equal("a8c4dd20-10ad-5f1e-86b3-9b0fdc54db57", finding.Context["samHoldingId"]);
        Assert.Equal("AH-01/036/8000", finding.Context["ctsLocationNumber"]);
    }

    [Fact]
    public void Reports_the_best_similarity_across_the_cts_lines()
    {
        // Line 2 is the nearer miss, so its score is the one worth triaging on.
        var finding = _rule.Evaluate(Records.Context(Named("Manhiers Farm", "THE PIGGERIES", "MANHEIRS FARM")));

        Assert.Equal("0.75", finding!.Context["nameSimilarity"]);
    }

    [Fact]
    public void Flags_when_sams_own_address_already_holds_the_cts_name()
    {
        var record = Named("Land At", "HIGH LANING FARM", "DENT") with { SamAddressName = "High Laning Farm" };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.NotNull(finding);
        Assert.Equal("placeholder", finding.Context["nameMismatchReason"]);
        Assert.Equal("High Laning Farm", finding.Context["samAddressName"]);
        Assert.Equal("true", finding.Context["samAddressNameMatchesCts"]);
    }

    [Fact]
    public void Says_so_when_sams_address_does_not_help_either()
    {
        var record = Named("Camphouse Farm", "HOLCOT MOORS FARM") with { SamAddressName = "Camphouse Farm" };

        var finding = _rule.Evaluate(Records.Context(record));

        Assert.Equal("false", finding!.Context["samAddressNameMatchesCts"]);
    }

    [Fact]
    public void Omits_the_address_keys_when_sam_records_no_address_name()
    {
        var finding = _rule.Evaluate(Records.Context(Named("Camphouse Farm", "HOLCOT MOORS FARM")));

        Assert.False(finding!.Context.ContainsKey("samAddressName"));
        Assert.False(finding.Context.ContainsKey("samAddressNameMatchesCts"));
    }

    [Fact]
    public void Keys_the_issue_on_the_rule_number_and_the_cph()
    {
        var finding = _rule.Evaluate(Records.Context(Named("Manhiers Farm", "MANHEIRS FARM")));

        Assert.Equal(IssueId.Derive("3", "01/036/8000"), finding!.IssueId);
        Assert.NotEqual(IssueId.Derive("1", "01/036/8000"), finding.IssueId);
    }

    [Fact]
    public void Assesses_a_record_without_the_engine()
    {
        var same = Rule03SamHoldingNameMismatch.Assess(Named("Wood Farm", "WOOD"));
        var different = Rule03SamHoldingNameMismatch.Assess(Named("Manhiers Farm", "MANHEIRS FARM"));
        var nothing = Rule03SamHoldingNameMismatch.Assess(Named("Ladymead Farm", "12 FORE STREET"));

        Assert.Equal(HoldingNameOutcome.Equivalent, same.Outcome);
        Assert.Equal(NameMatch.Generic, same.Match);
        Assert.False(same.IsIssue);

        Assert.Equal(HoldingNameOutcome.Different, different.Outcome);
        Assert.Equal(0.75, different.Similarity, 6);
        Assert.True(different.IsIssue);

        Assert.Equal(HoldingNameOutcome.NothingToCompare, nothing.Outcome);
        Assert.False(nothing.IsIssue);
    }

    private static CtsSamRecord Named(string? sam, string? cts, string? line2 = null) =>
        Records.InBoth() with
        {
            SamFeatureName = sam,
            CtsLocationName = cts,
            CtsLocationAddress2 = line2
        };
}
