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

    [Fact]
    public async Task Reads_the_address_groups_and_parties_rule_10_compares()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cleanse-synthetic-{Guid.NewGuid():N}.sqlite");
        await CreateSnapshotAsync(path);

        await using var source = new KrdsSqliteDataSource(new KrdsSnapshot(path, ownsFile: true, "synthetic"));

        var records = new Dictionary<string, CtsSamRecord>();
        await foreach (var record in source.StreamRecordsAsync(TestContext.Current.CancellationToken))
        {
            records[record.Cph.Value] = record;
        }

        var both = records["01/036/8000"];
        Assert.Equal("MK45 5WF", both.SamHoldingPostcode);
        Assert.Equal(new CtsAddress("WOOD FARM", "BEDFORD ROAD", "CLOPHILL", "BEDFORD", "BEDFORDSHIRE", "MK45 5WF"), both.CtsLocationAddress);
        Assert.Equal(new CtsAddress("THE ESTATE OFFICE", "HIGH STREET", "AMPTHILL", null, null, "MK45 2NA"), both.CtsCorrespondenceAddress);
        Assert.Equal("MK45 2NB", both.CtsContactPostCode);
        Assert.Equal(
            [
                new SamPartyAddress("C100", "holder", "Wood Farm", "Bedford Road", "Clophill", "BEDFORD", "MK45 5WF"),
                new SamPartyAddress("C100", "owner", "Wood Farm", "Bedford Road", "Clophill", "BEDFORD", "MK45 5WF"),
                new SamPartyAddress("C200", "keeper", null, "High Street", null, "AMPTHILL", "MK45 2NA")
            ],
            both.SamPartyAddresses);

        // Quotes and padding are stripped inside the packed party column as everywhere else, and a
        // holding with no parties, or no correspondence group, reads as empty rather than failing.
        var quoted = records["03/003/0003"];
        Assert.Null(quoted.SamHoldingPostcode);
        Assert.Null(quoted.CtsCorrespondenceAddress);
        Assert.Equal(new CtsAddress("HIGH LANING FARM", null, null, null, null, "LA10 5QJ"), quoted.CtsLocationAddress);
        var party = Assert.Single(quoted.SamPartyAddresses);
        Assert.Equal(new SamPartyAddress("C300", "holder", "High Laning Farm", null, null, "DENT", "LA10 5QJ"), party);

        var ctsOnly = records["02/100/0001"];
        Assert.Empty(ctsOnly.SamPartyAddresses);
        Assert.Null(ctsOnly.SamHoldingPostcode);
        Assert.Null(ctsOnly.CtsLocationAddress!.PostCode);
    }

    private static async Task CreateSnapshotAsync(string path)
    {
        await using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE Holding (
                Id TEXT NOT NULL, Cph TEXT, FeatureName TEXT, PaonDescription TEXT,
                Street TEXT, Town TEXT, Locality TEXT, Postcode TEXT);
            CREATE TABLE HoldingAnimalProfile (HoldingId TEXT NOT NULL, AnimalSpeciesCode TEXT);
            CREATE TABLE Party (
                Id TEXT NOT NULL, SourcePartyId TEXT, AddressLine1 TEXT, AddressStreet TEXT,
                AddressLocality TEXT, AddressTown TEXT, AddressPostcode TEXT);
            CREATE TABLE PartyRole (Id TEXT NOT NULL, PartyId TEXT, HoldingId TEXT, HerdId TEXT, Role TEXT);
            CREATE TABLE CtsOpenLocation (
                LocationNumber TEXT NOT NULL, Cph TEXT, PremisesType TEXT, CountyName TEXT,
                KeeperSurname TEXT, KeeperPostCode TEXT,
                LocationAddressName TEXT, LocationAddress2 TEXT, LocationAddress3 TEXT, LocationAddress4 TEXT, LocationAddress5 TEXT, LocationPostCode TEXT,
                CorrespondenceAddressName TEXT, CorrespondenceAddress2 TEXT, CorrespondenceAddress3 TEXT, CorrespondenceAddress4 TEXT, CorrespondenceAddress5 TEXT, CorrespondencePostCode TEXT,
                ContactPostCode TEXT);

            INSERT INTO Holding (Id, Cph, FeatureName, PaonDescription, Street, Town, Locality, Postcode) VALUES
                ('h1', '01/036/8000', 'Wood Farm', 'Wood Farm', 'Bedford Road', 'BEDFORD', 'Clophill', 'MK45 5WF'),
                ('h3', '03/003/0003', ' ''Land At'' ', '''''', NULL, NULL, NULL, '''''');
            INSERT INTO HoldingAnimalProfile VALUES ('h1', 'CTT'), ('h1', 'SHP'), ('h1', 'CTT');
            INSERT INTO Party (Id, SourcePartyId, AddressLine1, AddressStreet, AddressLocality, AddressTown, AddressPostcode) VALUES
                ('p1', 'C100', 'Wood Farm', 'Bedford Road', 'Clophill', 'BEDFORD', 'MK45 5WF'),
                ('p2', 'C200', NULL, 'High Street', '', 'AMPTHILL', 'MK45 2NA'),
                ('p3', 'C300', ' ''High Laning Farm'' ', NULL, NULL, 'DENT', ' LA10 5QJ ');
            INSERT INTO PartyRole (Id, PartyId, HoldingId, HerdId, Role) VALUES
                ('r1', 'p1', 'h1', NULL, 'holder'),
                ('r2', 'p1', 'h1', 'herd-1', 'owner'),
                ('r3', 'p1', 'h1', 'herd-2', 'owner'),
                ('r4', 'p2', 'h1', 'herd-1', 'keeper'),
                ('r5', 'p3', 'h3', NULL, 'holder');
            INSERT INTO CtsOpenLocation (LocationNumber, Cph, PremisesType, CountyName, KeeperSurname, KeeperPostCode,
                LocationAddressName, LocationAddress2, LocationAddress3, LocationAddress4, LocationAddress5, LocationPostCode,
                CorrespondenceAddressName, CorrespondenceAddress2, CorrespondenceAddress3, CorrespondenceAddress4, CorrespondenceAddress5, CorrespondencePostCode,
                ContactPostCode) VALUES
                ('AH-01/036/8000', '01/036/8000', 'AH', 'Bedfordshire', 'HEYWOOD LTD', 'MK45 5WF',
                    'WOOD FARM', 'BEDFORD ROAD', 'CLOPHILL', 'BEDFORD', 'BEDFORDSHIRE', 'MK45 5WF',
                    'THE ESTATE OFFICE', 'HIGH STREET', 'AMPTHILL', '', NULL, 'MK45 2NA',
                    'MK45 2NB'),
                ('AH-02/100/0001', '02/100/0001', 'AH', 'Bedfordshire', 'SMITH', 'MK45 5WG',
                    'T/A J SMITH & SONS', '', NULL, NULL, NULL, NULL,
                    NULL, NULL, NULL, NULL, NULL, NULL,
                    NULL),
                ('AH-03/003/0003', '03/003/0003', 'AH', 'Cumbria', 'DENT', 'LA10 5QJ',
                    '''HIGH LANING FARM''', '   ', NULL, NULL, NULL, '''LA10 5QJ''',
                    '''''', '   ', NULL, NULL, NULL, NULL,
                    NULL),
                ('SH-1234', '1234', 'SH', 'Cumbria', NULL, NULL,
                    'ABATTOIR', NULL, NULL, NULL, NULL, NULL,
                    NULL, NULL, NULL, NULL, NULL, NULL,
                    NULL);
            """;

        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        SqliteConnection.ClearPool(connection);
    }
}
