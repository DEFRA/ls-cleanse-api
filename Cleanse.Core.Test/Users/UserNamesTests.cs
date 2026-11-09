using Cleanse.Core.Users;

namespace Cleanse.Core.Test.Users;

public sealed class UserNamesTests
{
    [Theory]
    [InlineData("kris.dyson@defra.gov.uk", "Kris Dyson")]
    [InlineData("Andrew.Agius@apha.gov.uk", "Andrew Agius")]
    [InlineData("Owen.Jones@apha.gov.uk", "Owen Jones")]
    [InlineData("Kyle.Lloyd2@apha.gov.uk", "Kyle Lloyd")]
    [InlineData("Dafydd.Lovesey@apha.gov.uk", "Dafydd Lovesey")]
    [InlineData("Yousef.Khan@apha.gov.uk", "Yousef Khan")]
    [InlineData("Liz.Ellaway@apha.gov.uk", "Liz Ellaway")]
    [InlineData("seb.paterson@defra.gov.uk", "Seb Paterson")]
    [InlineData("StJohn.Hawkins1@defra.gov.uk", "StJohn Hawkins")]
    [InlineData("Matt.Nodder@defra.gov.uk", "Matt Nodder")]
    [InlineData("Alexandra.Whittle@apha.gov.uk", "Alexandra Whittle")]
    [InlineData("Cat.Beckett@defra.gov.uk", "Cat Beckett")]
    [InlineData("Jen.Graham@defra.gov.uk", "Jen Graham")]
    public void The_initial_users_get_their_names_from_their_addresses(string email, string expected) =>
        Assert.Equal(expected, UserNames.FromEmail(email));

    [Theory]
    [InlineData("KRIS.DYSON@DEFRA.GOV.UK", "Kris Dyson")]
    [InlineData("mary-jane_o'neill@example.test", "Mary-Jane O'Neill")]
    [InlineData("kris.dyson+test@example.test", "Kris Dyson")]
    [InlineData("jsmith@example.test", "Jsmith")]
    [InlineData("12345@example.test", "12345")]
    public void Unusual_addresses_still_give_a_readable_name(string email, string expected) =>
        Assert.Equal(expected, UserNames.FromEmail(email));

    [Theory]
    [InlineData(" Kyle.Lloyd2@APHA.gov.uk ", "kyle.lloyd2@apha.gov.uk")]
    [InlineData("a@b.gov.uk", "a@b.gov.uk")]
    public void Addresses_are_trimmed_and_lower_cased(string value, string expected)
    {
        Assert.True(EmailAddress.TryNormalise(value, out var email));
        Assert.Equal(expected, email);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    [InlineData("Kyle Lloyd")]
    [InlineData("kyle@localhost")]
    [InlineData("@apha.gov.uk")]
    [InlineData("kyle@@apha.gov.uk")]
    [InlineData("kyle@apha.gov.uk.")]
    [InlineData("Kyle <kyle@apha.gov.uk>")]
    [InlineData("kyle@apha.gov.uk; owen@apha.gov.uk")]
    public void Names_and_malformed_addresses_are_not_email_addresses(string? value) =>
        Assert.False(EmailAddress.TryNormalise(value, out _));
}
