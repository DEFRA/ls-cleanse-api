namespace Cleanse.Rules.Domain;

/// <summary>
/// One CTS open location joined to its SAM holding, if SAM has one.
/// </summary>
/// <remarks>
/// This is the unit of work for the CTS primary pass: the adapter produces it, and every rule in
/// the pass sees exactly this and nothing else. The SAM side is nullable because its absence is
/// itself what rule 2 detects.
/// </remarks>
public sealed record CtsSamRecord
{
    public required string CtsLocationNumber { get; init; }

    public required Cph Cph { get; init; }

    public string? CtsPremisesType { get; init; }

    public string? CtsCountyName { get; init; }

    public string? CtsKeeperSurname { get; init; }

    public string? CtsKeeperPostCode { get; init; }

    /// <summary>The CTS location's name line (<c>LocationAddressName</c>). Upper case in the source.</summary>
    public string? CtsLocationName { get; init; }

    /// <summary>
    /// The second line of the CTS location address. CTS often files the holding name here when
    /// the name line is a trading name, a care-of line or a building.
    /// </summary>
    public string? CtsLocationAddress2 { get; init; }

    /// <summary>Null when SAM holds no holding for this CPH.</summary>
    public string? SamHoldingId { get; init; }

    public string? SamFeatureName { get; init; }

    /// <summary>
    /// SAM's address building name (<c>PaonDescription</c>), as distinct from the holding's
    /// <see cref="SamFeatureName"/>. Null when SAM records none.
    /// </summary>
    public string? SamAddressName { get; init; }

    public string? SamStreet { get; init; }

    public string? SamTown { get; init; }

    public string? SamLocality { get; init; }

    /// <summary>The distinct animal species codes SAM records against the holding.</summary>
    public IReadOnlySet<string> SamSpeciesCodes { get; init; } =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);
}
