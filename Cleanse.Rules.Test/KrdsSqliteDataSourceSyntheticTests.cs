using Cleanse.Rules.Domain;
using Cleanse.Rules.Krds;
using Cleanse.Rules.Krds.Snapshots;
using Microsoft.Data.Sqlite;

namespace Cleanse.Rules.Test;

/// <summary>
/// Reads a snapshot built here, so the adapter's column mapping is checked on every machine and
/// not only where a real KRDS snapshot happens to be present.
/// </summary>
public class KrdsSqliteDataSourceSyntheticTests
{
    [Fact]
    public async Task Reads_the_name_and_address_fields_both_rules_compare()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cleanse-synthetic-{Guid.NewGuid():N}.sqlite");
        await CreateSnapshotAsync(path);

        await using var source = new KrdsSqliteDataSource(new KrdsSnapshot(path, ownsFile: true, "synthetic"));

        var records = new List<CtsSamRecord>();
        await foreach (var record in source.StreamRecordsAsync(TestContext.Current.CancellationToken))
        {
            records.Add(record);
        }

        var both = Assert.Single(records, r => r.Cph.Value == "01/036/8000");
        Assert.Equal("Wood Farm", both.SamFeatureName);
        Assert.Equal("WOOD FARM", both.CtsLocationName);
        Assert.Equal("BEDFORD ROAD", both.CtsLocationAddress2);
        Assert.Equal("Wood Farm", both.SamAddressName);
        Assert.Equal("Bedford Road", both.SamStreet);
        Assert.Equal("BEDFORD", both.SamTown);
        Assert.Equal("Clophill", both.SamLocality);
        Assert.Equal(["CTT", "SHP"], both.SamSpeciesCodes.Order(StringComparer.Ordinal));

        var ctsOnly = Assert.Single(records, r => r.Cph.Value == "02/100/0001");
        Assert.Null(ctsOnly.SamHoldingId);
        Assert.Null(ctsOnly.SamFeatureName);
        Assert.Null(ctsOnly.SamAddressName);
        Assert.Equal("T/A J SMITH & SONS", ctsOnly.CtsLocationName);
        Assert.Null(ctsOnly.CtsLocationAddress2);
    }

    [Fact]
    public async Task Strips_the_quotes_and_whitespace_the_export_wraps_values_in()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cleanse-synthetic-{Guid.NewGuid():N}.sqlite");
        await CreateSnapshotAsync(path);

        await using var source = new KrdsSqliteDataSource(new KrdsSnapshot(path, ownsFile: true, "synthetic"));

        await foreach (var record in source.StreamRecordsAsync(TestContext.Current.CancellationToken))
        {
            if (record.Cph.Value != "03/003/0003")
            {
                continue;
            }

            Assert.Equal("Land At", record.SamFeatureName);
            Assert.Equal("HIGH LANING FARM", record.CtsLocationName);
            Assert.Null(record.CtsLocationAddress2);
            Assert.Null(record.SamAddressName);
            return;
        }

        Assert.Fail("The quoted record was not streamed.");
    }

    private static async Task CreateSnapshotAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Holding (
                Id TEXT NOT NULL, Cph TEXT, FeatureName TEXT, PaonDescription TEXT,
                Street TEXT, Town TEXT, Locality TEXT);
            CREATE TABLE HoldingAnimalProfile (HoldingId TEXT NOT NULL, AnimalSpeciesCode TEXT);
            CREATE TABLE CtsOpenLocation (
                LocationNumber TEXT NOT NULL, Cph TEXT, PremisesType TEXT, CountyName TEXT,
                KeeperSurname TEXT, KeeperPostCode TEXT, LocationAddressName TEXT, LocationAddress2 TEXT);

            INSERT INTO Holding VALUES
                ('h1', '01/036/8000', 'Wood Farm', 'Wood Farm', 'Bedford Road', 'BEDFORD', 'Clophill'),
                ('h3', '03/003/0003', ' ''Land At'' ', '''''', NULL, NULL, NULL);
            INSERT INTO HoldingAnimalProfile VALUES ('h1', 'CTT'), ('h1', 'SHP'), ('h1', 'CTT');
            INSERT INTO CtsOpenLocation VALUES
                ('AH-01/036/8000', '01/036/8000', 'AH', 'Bedfordshire', 'HEYWOOD LTD', 'MK45 5WF', 'WOOD FARM', 'BEDFORD ROAD'),
                ('AH-02/100/0001', '02/100/0001', 'AH', 'Bedfordshire', 'SMITH', 'MK45 5WG', 'T/A J SMITH & SONS', ''),
                ('AH-03/003/0003', '03/003/0003', 'AH', 'Cumbria', 'DENT', 'LA10 5QJ', '''HIGH LANING FARM''', '   '),
                ('SH-1234', '1234', 'SH', 'Cumbria', NULL, NULL, 'ABATTOIR', NULL);
            """;

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        SqliteConnection.ClearPool(connection);
    }
}
