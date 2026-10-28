using Cleanse.Rules.Groups.CtsSam.Rules;

namespace Cleanse.Rules.Test;

public class HoldingNameEquivalenceTests
{
    [Theory]
    [InlineData("Spring Well Farm", "spring well farm")]
    [InlineData("SPRINGWELL FARM", "springwell farm")]
    [InlineData("St. Giles Farm", "st giles farm")]
    [InlineData("Thompson's Walls", "thompsons walls")]
    [InlineData("Bentham & District Auction Mart", "bentham and district auction mart")]
    [InlineData("Holt Fm", "holt farm")]
    [InlineData("TB ISO Unit @ Wadfield Farm", "tb isolation unit wadfield farm")]
    [InlineData("Land At Manor Farm", "manor farm")]
    [InlineData("LAND @ MANOR FARM", "manor farm")]
    [InlineData("On Land At Church Farm", "church farm")]
    [InlineData("Fields Near Mill Wood", "mill wood")]
    [InlineData("Plot Of Land Adjacent To Mere Lane", "mere lane")]
    [InlineData("Land To The Rear Of Greenbough Cottage", "greenbough cottage")]
    [InlineData("Additional CPH @ Hill Farm", "hill farm")]
    [InlineData("LAND AT HALL FARM, THRIGBY", "hall farm thrigby")]
    [InlineData("T/A STOWFORD FARM", "stowford farm")]
    [InlineData("C/O MUTTON HALL FARM", "mutton hall farm")]
    [InlineData("Trading As Keybridge Farms Ltd", "keybridge farms")]
    [InlineData("12 Fore Street", "fore street")]
    [InlineData("9A Old Mill Lane", "old mill lane")]
    [InlineData("The Grange", "grange")]
    [InlineData("Ells Farm LLP", "ells farm")]
    public void Canonicalises_names_to_the_words_that_identify_the_place(string name, string expected)
    {
        Assert.Equal(expected, string.Join(' ', HoldingNameEquivalence.Canonicalise(name)));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Land At")]
    [InlineData("LAND @")]
    [InlineData("Fields At")]
    [InlineData("Land Off")]
    [InlineData("The Land At")]
    public void Canonicalises_a_name_that_identifies_nothing_to_no_words(string? name)
    {
        Assert.Empty(HoldingNameEquivalence.Canonicalise(name));
    }

    [Fact]
    public void Keeps_a_number_that_is_the_whole_name()
    {
        // A bare number is a name of sorts; only a house number before a street is dropped.
        Assert.Equal(["375"], HoldingNameEquivalence.Canonicalise("375 "));
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("n/a", true)]
    [InlineData("N/A", true)]
    [InlineData("Unknown", true)]
    [InlineData("Not Known", true)]
    [InlineData("tbc", true)]
    [InlineData("-", true)]
    [InlineData("Land At", true)]
    [InlineData("Fields @", true)]
    [InlineData("Wood Farm", false)]
    [InlineData("Main Field", false)]
    [InlineData("Land At Wood Farm", false)]
    public void Recognises_placeholders(string? name, bool expected)
    {
        Assert.Equal(expected, HoldingNameEquivalence.IsPlaceholder(name));
    }

    [Theory]
    [InlineData("Spring Well Farm", "SPRINGWELL FARM", NameMatch.Canonical)]
    [InlineData("Holt Farm", "HOLT FM", NameMatch.Canonical)]
    [InlineData("Land At Manor Farm", "MANOR FARM", NameMatch.Canonical)]
    [InlineData("Stowford Farm", "T/A STOWFORD FARM", NameMatch.Canonical)]
    [InlineData("Rose Cottage", "15 ROSE COTTAGE", NameMatch.Canonical)]
    [InlineData("The Shrewsbury Auction Centre", "SHREWSBURY AUCTION CENTRE", NameMatch.Canonical)]
    [InlineData("Marley Farm", "MARLEY", NameMatch.Generic)]
    [InlineData("High Loanthwaite", "HIGH LOANTHWAITE FARM", NameMatch.Generic)]
    [InlineData("West Barn Gatesbury Farm", "WEST BARN", NameMatch.Contains)]
    [InlineData("Middle Farm", "MIDDLE FARM SHREWTON", NameMatch.Contains)]
    [InlineData("Home Farm", "REDLYNCH HOME FARM", NameMatch.Contains)]
    [InlineData("Fox Covet Farm", "FOX COVERT FARM", NameMatch.Spelling)]
    [InlineData("Tregellis Farm", "TREGILLIS FARM", NameMatch.Spelling)]
    [InlineData("Burcot Farm", "BURCOTT FARM", NameMatch.Spelling)]
    [InlineData("Halmore Farm", "MANOR FARM", NameMatch.Different)]
    [InlineData("Castle Farm", "THE BELVOIR ESTATE", NameMatch.Different)]
    [InlineData("Smithy Farm", "DIAL LANE", NameMatch.Different)]
    [InlineData("Manhiers Farm", "MANHEIRS FARM", NameMatch.Different)]
    public void Decides_whether_two_names_are_the_same_place(string sam, string cts, NameMatch expected)
    {
        var comparison = Compare(sam, cts, allowContainment: true);

        Assert.Equal(expected, comparison.Match);
        Assert.Equal(expected != NameMatch.Different, comparison.IsEquivalent);
    }

    [Theory]
    [InlineData("East Stoney Keld", "WEST STONEY KELD")]
    [InlineData("Blunderfield West", "BLUNDERFIELD EAST")]
    public void Does_not_let_spelling_tolerance_pass_a_swapped_qualifier(string sam, string cts)
    {
        var comparison = Compare(sam, cts, allowContainment: true);

        Assert.Equal(NameMatch.Different, comparison.Match);
        Assert.True(comparison.Similarity >= HoldingNameEquivalence.SpellingThreshold, "the guard, not the threshold, must be what rejects these");
    }

    [Fact]
    public void Tolerates_a_qualifier_that_only_one_side_has()
    {
        // Only a swap marks a different place; one side simply saying more is a spelling matter.
        Assert.Equal(NameMatch.Spelling, Compare("Upper Houses Farm", "UPPERHOUSE FARM", allowContainment: true).Match);
    }

    [Fact]
    public void Containment_is_only_applied_where_the_caller_allows_it()
    {
        // Address line 2 is usually the village, so "Land @ Bintree & North Elmham" must not match "BINTREE" there.
        Assert.Equal(NameMatch.Contains, Compare("Land @ Bintree & North Elmham", "BINTREE", allowContainment: true).Match);
        Assert.Equal(NameMatch.Different, Compare("Land @ Bintree & North Elmham", "BINTREE", allowContainment: false).Match);
    }

    [Fact]
    public void Compares_nothing_with_an_empty_name()
    {
        var comparison = HoldingNameEquivalence.Compare([], ["manor", "farm"], allowContainment: true);

        Assert.Equal(NameMatch.Different, comparison.Match);
        Assert.Equal(0d, comparison.Similarity);
    }

    [Fact]
    public void Scores_similarity_on_the_distinctive_words_only()
    {
        // "foxcovet" vs "foxcovert": one insertion in nine characters.
        Assert.Equal(1d - 1d / 9d, Compare("Fox Covet Farm", "FOX COVERT FARM", allowContainment: true).Similarity, 6);

        // Different places that whole-string scoring would call similar: "halmore" vs "manor".
        Assert.True(Compare("Halmore Farm", "MANOR FARM", allowContainment: true).Similarity < 0.6);
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("", "abc", 3)]
    [InlineData("abc", "", 3)]
    [InlineData("same", "same", 0)]
    [InlineData("flaw", "lawn", 2)]
    public void Counts_single_character_edits(string a, string b, int expected)
    {
        Assert.Equal(expected, HoldingNameEquivalence.LevenshteinDistance(a, b));
    }

    [Fact]
    public void Similarity_of_two_empty_strings_is_total()
    {
        Assert.Equal(1d, HoldingNameEquivalence.Similarity(string.Empty, string.Empty));
    }

    [Theory]
    [InlineData("MANOR FARM", null, null, null, true)]
    [InlineData("SADBOROW", "Sadborow", null, null, true)]
    [InlineData("WOOD LANE", "Wood Lane Farm", null, null, true)]
    [InlineData("12 FORE STREET", null, null, null, false)]
    [InlineData("T/A J SMITH & SONS", null, null, null, false)]
    [InlineData("C/O MR S J BISH", null, null, null, false)]
    [InlineData("P/A TREVILLADOR", null, null, null, false)]
    [InlineData("LAND AT", null, null, null, false)]
    [InlineData(null, null, null, null, false)]
    [InlineData("WOOD LANE", "Wood Lane", null, null, false)]
    [InlineData("ROGATE", null, "PETERSFIELD", "Rogate", false)]
    [InlineData("PETERSFIELD", null, "PETERSFIELD", "Rogate", false)]
    public void Tells_a_place_name_from_an_address(string? ctsName, string? street, string? town, string? locality, bool expected)
    {
        Assert.Equal(expected, HoldingNameEquivalence.IsPlaceName(ctsName, street, town, locality));
    }

    [Fact]
    public void Drops_generic_words_unless_nothing_would_remain()
    {
        Assert.Equal(["marley"], HoldingNameEquivalence.Distinctive(["marley", "farm"]));
        Assert.Equal(["farm"], HoldingNameEquivalence.Distinctive(["farm"]));
    }

    private static NameComparison Compare(string sam, string cts, bool allowContainment) =>
        HoldingNameEquivalence.Compare(
            HoldingNameEquivalence.Canonicalise(sam),
            HoldingNameEquivalence.Canonicalise(cts),
            allowContainment);
}
