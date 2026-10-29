namespace Cleanse.Rules.Domain;

/// <summary>
/// One of the address groups a CTS open location carries: a name line, up to four further lines
/// and a postcode. CTS files the same shape four times over (location, keeper, correspondence,
/// contact), so the groups share a type.
/// </summary>
/// <remarks>
/// Values are as the adapter read them: trimmed, quotes stripped, null when blank. The record is
/// null on <see cref="CtsSamRecord"/> when CTS recorded nothing in that group at all; a group with
/// only a postcode is still a group.
/// </remarks>
public sealed record CtsAddress(
    string? Name,
    string? Line2,
    string? Line3,
    string? Line4,
    string? Line5,
    string? PostCode)
{
    /// <summary>The populated address lines, in order, without the postcode.</summary>
    public IReadOnlyList<string> Lines =>
        new[] { Name, Line2, Line3, Line4, Line5 }.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line!).ToList();

    public bool IsEmpty => Lines.Count == 0 && string.IsNullOrWhiteSpace(PostCode);

    /// <summary>Null rather than an empty group, so callers can coalesce.</summary>
    public static CtsAddress? FromLines(string? name, string? line2, string? line3, string? line4, string? line5, string? postCode)
    {
        var address = new CtsAddress(name, line2, line3, line4, line5, postCode);
        return address.IsEmpty ? null : address;
    }
}
