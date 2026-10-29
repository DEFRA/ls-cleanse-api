using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Groups.CtsSam;

namespace Cleanse.Rules.Test.Support;

internal static class Records
{
    /// <summary>
    /// A CTS location present in both systems, with a cattle unit in SAM. It has no CTS name line
    /// (rule 3's tests rely on that) and the holder writes from the holding, at the postcode CTS
    /// has, so it is clean for rule 10 too.
    /// </summary>
    public static CtsSamRecord InBoth(string cph = "01/036/8000", params string[] speciesCodes) => new()
    {
        CtsLocationNumber = $"AH-{cph}",
        Cph = Cph.Parse(cph),
        CtsPremisesType = "AH",
        CtsCountyName = "Bedfordshire",
        CtsKeeperSurname = "HEYWOOD LTD",
        CtsKeeperPostCode = "MK45 5WF",
        CtsLocationAddress = new CtsAddress(null, null, "CLOPHILL", "BEDFORD", null, "MK45 5WF"),
        SamHoldingId = "a8c4dd20-10ad-5f1e-86b3-9b0fdc54db57",
        SamFeatureName = "Wood Farm",
        SamHoldingPostcode = "MK45 5WF",
        SamPartyAddresses = [Holder("C100", "MK45 5WF")],
        SamSpeciesCodes = new HashSet<string>(
            speciesCodes.Length == 0 ? ["CTT"] : speciesCodes, StringComparer.OrdinalIgnoreCase)
    };

    public static SamPartyAddress Holder(string partyId, string? postcode, string? line1 = "Wood Farm") =>
        Party(partyId, SamPartyAddress.HolderRole, postcode, line1);

    public static SamPartyAddress Party(string partyId, string role, string? postcode, string? line1 = "Wood Farm") =>
        new(partyId, role, line1, "Bedford Road", "Clophill", "BEDFORD", postcode);

    /// <summary>A CTS location with no matching SAM holding.</summary>
    public static CtsSamRecord CtsOnly(string cph = "02/100/0001") => new()
    {
        CtsLocationNumber = $"AH-{cph}",
        Cph = Cph.Parse(cph),
        CtsPremisesType = "AH",
        CtsCountyName = "Bedfordshire"
    };

    public static CtsSamRuleContext Context(CtsSamRecord record) => new(record);
}

/// <summary>Serves a mutable set of records, so engine tests need no database at all.</summary>
internal sealed class FakeDataSource(params CtsSamRecord[] records)
    : ICtsSamDataSource, ICtsSamDataSourceFactory
{
    public bool Disposed { get; private set; }

    /// <summary>Swap between runs to simulate the upstream data changing overnight.</summary>
    public IReadOnlyList<CtsSamRecord> Records { get; set; } = records;

    /// <summary>Set to make the next run fail while acquiring its snapshot.</summary>
    public Exception? FailOnOpen { get; set; }

    public string Description => "fake";

    public Task<ICtsSamDataSource> OpenAsync(CancellationToken cancellationToken = default) =>
        FailOnOpen is null ? Task.FromResult<ICtsSamDataSource>(this) : throw FailOnOpen;

    public Task<int> CountRecordsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Records.Count);

    public async IAsyncEnumerable<CtsSamRecord> StreamRecordsAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        foreach (var record in Records)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return record;
            await Task.Yield();
        }
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}
