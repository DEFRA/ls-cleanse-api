using System.Runtime.CompilerServices;
using Cleanse.Rules.Abstractions;
using Cleanse.Rules.Domain;
using Cleanse.Rules.Krds.Snapshots;
using Microsoft.Data.Sqlite;

namespace Cleanse.Rules.Krds;

/// <summary>
/// Reads CTS open locations and their SAM holdings from a KRDS SQLite snapshot.
/// </summary>
/// <remarks>
/// One query does the whole primary pass. The SAM holding is a left join so that its absence —
/// which is what rule 2 detects — arrives as a null rather than a missing row, and the species
/// codes and party addresses come from correlated subqueries rather than a second round trip per
/// record, so the pass costs one scan instead of one query per holding.
/// </remarks>
internal sealed class KrdsSqliteDataSource : ICtsSamDataSource
{
    /// <summary>
    /// Restricts the pass to real CPHs. CTS files slaughterhouses in the same dataset under a
    /// bare number, so this pattern is also the slaughterhouse exclusion.
    /// </summary>
    internal const string CphPattern = "[0-9][0-9]/[0-9][0-9][0-9]/[0-9][0-9][0-9][0-9]";

    /// <summary>Separate the parties of a holding, and the fields of a party, in the packed column.</summary>
    private const char PartySeparator = '\u001e';
    private const char FieldSeparator = '\u001f';

    private const string CountSql = $"""
        SELECT COUNT(*) FROM CtsOpenLocation WHERE Cph GLOB '{CphPattern}'
        """;

    private const string StreamSql = $"""
        SELECT c.LocationNumber,
               c.Cph,
               c.PremisesType,
               c.CountyName,
               c.KeeperSurname,
               c.KeeperPostCode,
               h.Id,
               h.FeatureName,
               (SELECT GROUP_CONCAT(DISTINCT p.AnimalSpeciesCode)
                  FROM HoldingAnimalProfile p
                 WHERE p.HoldingId = h.Id),
               c.LocationAddressName,
               c.LocationAddress2,
               h.PaonDescription,
               h.Street,
               h.Town,
               h.Locality,
               h.Postcode,
               c.LocationAddress3,
               c.LocationAddress4,
               c.LocationAddress5,
               c.LocationPostCode,
               c.CorrespondenceAddressName,
               c.CorrespondenceAddress2,
               c.CorrespondenceAddress3,
               c.CorrespondenceAddress4,
               c.CorrespondenceAddress5,
               c.CorrespondencePostCode,
               c.ContactPostCode,
               (SELECT GROUP_CONCAT(party, char(30))
                  FROM (SELECT DISTINCT COALESCE(p.SourcePartyId, p.Id) || char(31) || pr.Role || char(31)
                                        || COALESCE(p.AddressLine1, '') || char(31) || COALESCE(p.AddressStreet, '') || char(31)
                                        || COALESCE(p.AddressLocality, '') || char(31) || COALESCE(p.AddressTown, '') || char(31)
                                        || COALESCE(p.AddressPostcode, '') AS party
                          FROM PartyRole pr
                          JOIN Party p ON p.Id = pr.PartyId
                         WHERE pr.HoldingId = h.Id))
          FROM CtsOpenLocation c
          LEFT JOIN Holding h ON h.Cph = c.Cph
         WHERE c.Cph GLOB '{CphPattern}'
         ORDER BY c.LocationNumber
        """;

    private readonly KrdsSnapshot _snapshot;
    private readonly string _connectionString;

    public KrdsSqliteDataSource(KrdsSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        _snapshot = snapshot;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = snapshot.FilePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<int> CountRecordsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = CountSql;

        var value = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(value ?? 0);
    }

    public async IAsyncEnumerable<CtsSamRecord> StreamRecordsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = StreamSql;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var raw = reader.GetString(1);
            if (!Cph.TryParse(raw, out var cph))
            {
                // The GLOB already excluded these; belt and braces against a schema change.
                continue;
            }

            yield return new CtsSamRecord
            {
                CtsLocationNumber = reader.GetString(0),
                Cph = cph,
                CtsPremisesType = Text(reader, 2),
                CtsCountyName = Text(reader, 3),
                CtsKeeperSurname = Text(reader, 4),
                CtsKeeperPostCode = Text(reader, 5),
                SamHoldingId = Text(reader, 6),
                SamFeatureName = Text(reader, 7),
                SamSpeciesCodes = SpeciesCodes(Text(reader, 8)),
                CtsLocationName = Text(reader, 9),
                CtsLocationAddress2 = Text(reader, 10),
                SamAddressName = Text(reader, 11),
                SamStreet = Text(reader, 12),
                SamTown = Text(reader, 13),
                SamLocality = Text(reader, 14),
                SamHoldingPostcode = Text(reader, 15),
                CtsLocationAddress = CtsAddress.FromLines(
                    Text(reader, 9), Text(reader, 10), Text(reader, 16), Text(reader, 17), Text(reader, 18), Text(reader, 19)),
                CtsCorrespondenceAddress = CtsAddress.FromLines(
                    Text(reader, 20), Text(reader, 21), Text(reader, 22), Text(reader, 23), Text(reader, 24), Text(reader, 25)),
                CtsContactPostCode = Text(reader, 26),
                SamPartyAddresses = PartyAddresses(reader.IsDBNull(27) ? null : reader.GetString(27))
            };
        }
    }

    public ValueTask DisposeAsync()
    {
        SqliteConnection.ClearPool(new SqliteConnection(_connectionString));
        return _snapshot.DisposeAsync();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    /// <summary>
    /// Trims, and strips the literal quote characters the upstream export wraps some values in.
    /// </summary>
    private static string? Text(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : Clean(reader.GetString(ordinal));

    private static string? Clean(string value)
    {
        var cleaned = value.Trim().Trim('\'').Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    /// <summary>
    /// Unpacks the party column: one party per record separator, fields in query order. Sorted
    /// here because SQLite promises no order inside an aggregate.
    /// </summary>
    private static IReadOnlyList<SamPartyAddress> PartyAddresses(string? packed)
    {
        if (string.IsNullOrEmpty(packed))
        {
            return [];
        }

        var parties = new List<SamPartyAddress>();
        foreach (var entry in packed.Split(PartySeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var fields = entry.Split(FieldSeparator);
            if (fields.Length != 7 || Clean(fields[0]) is not { } partyId || Clean(fields[1]) is not { } role)
            {
                // Not something this adapter wrote; belt and braces against a schema change.
                continue;
            }

            parties.Add(new SamPartyAddress(
                partyId,
                role,
                Clean(fields[2]),
                Clean(fields[3]),
                Clean(fields[4]),
                Clean(fields[5]),
                Clean(fields[6])));
        }

        return [.. parties
            .OrderBy(party => party.SourcePartyId, StringComparer.Ordinal)
            .ThenBy(party => party.Role, StringComparer.Ordinal)];
    }

    private static IReadOnlySet<string> SpeciesCodes(string? concatenated)
    {
        var codes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(concatenated))
        {
            return codes;
        }

        foreach (var code in concatenated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            codes.Add(code);
        }

        return codes;
    }
}
