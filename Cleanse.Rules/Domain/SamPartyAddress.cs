namespace Cleanse.Rules.Domain;

/// <summary>
/// The contact address SAM holds for one party involved with a holding, as KRDS shapes it.
/// </summary>
/// <remarks>
/// <see cref="Role"/> is the KRDS party role: <c>holder</c> is the CPH holdership, <c>keeper</c>
/// and <c>owner</c> are livestock roles. The same party can appear once per role. KRDS composes
/// <see cref="Line1"/> from the premises description and drops the house number, so a numbered
/// address often has no first line at all; the postcode is the dependable part.
/// </remarks>
public sealed record SamPartyAddress(
    string SourcePartyId,
    string Role,
    string? Line1,
    string? Street,
    string? Locality,
    string? Town,
    string? Postcode)
{
    public const string HolderRole = "holder";
    public const string KeeperRole = "keeper";
    public const string OwnerRole = "owner";

    /// <summary>The populated address lines, in order, without the postcode.</summary>
    public IReadOnlyList<string> Lines =>
        new[] { Line1, Street, Locality, Town }.Where(line => !string.IsNullOrWhiteSpace(line)).Select(line => line!).ToList();
}
