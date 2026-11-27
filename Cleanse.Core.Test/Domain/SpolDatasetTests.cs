using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Datasets;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Domain;

public sealed class SpolDatasetTests
{
    private static SpolDataset NewDataset(string? name = "November export") =>
        SpolDataset.Create(Guid.NewGuid(), name, SpolDatasetKey.Hash("key"), TestData.Alice, TestData.Night1).Value;

    [Fact]
    public void A_new_dataset_is_empty_and_records_who_created_it()
    {
        var dataset = NewDataset("  November export  ");

        Assert.Equal("November export", dataset.Name);
        Assert.Equal(SpolDatasetStatus.Empty, dataset.Status);
        Assert.Equal(0, dataset.RowCount);
        Assert.Equal(TestData.Alice.Id, dataset.CreatedById);
        Assert.Equal(TestData.Alice.DisplayName, dataset.CreatedByName);
        Assert.Equal(1, dataset.Version);
    }

    [Theory]
    [InlineData("2026-01-10T02:00:00Z", "SPOL 10 Jan 2026 02:00")]
    [InlineData("2026-07-10T02:00:00Z", "SPOL 10 Jul 2026 03:00")]
    public void Without_a_name_a_dataset_is_named_after_when_it_was_created_in_uk_time(string at, string expected)
    {
        var dataset = SpolDataset.Create(Guid.NewGuid(), " ", SpolDatasetKey.Hash("key"), TestData.Alice,
            DateTimeOffset.Parse(at, System.Globalization.CultureInfo.InvariantCulture)).Value;

        Assert.Equal(expected, dataset.Name);
    }

    [Fact]
    public void A_name_has_at_most_100_characters()
    {
        Assert.True(SpolDataset.Create(Guid.NewGuid(), new string('x', 101), "hash", TestData.Alice, TestData.Night1).IsFailure);
        Assert.True(NewDataset().Rename(new string('x', 101), TestData.Night1).IsFailure);
        Assert.True(NewDataset().Rename("  ", TestData.Night1).IsFailure);
    }

    [Fact]
    public void Renaming_moves_the_version_on_unless_the_name_is_the_same()
    {
        var dataset = NewDataset();
        var later = TestData.Night1.AddHours(1);

        Assert.True(dataset.Rename(" November export ", later).IsSuccess);
        Assert.Equal(1, dataset.Version);

        Assert.True(dataset.Rename("SPOL 27 Nov", later).IsSuccess);
        Assert.Equal("SPOL 27 Nov", dataset.Name);
        Assert.Equal(later, dataset.UpdatedAtUtc);
        Assert.Equal(2, dataset.Version);
    }

    [Fact]
    public void The_first_page_of_a_load_empties_the_dataset_and_later_pages_keep_its_rows()
    {
        var dataset = NewDataset();
        var load = Guid.NewGuid();

        Assert.True(dataset.AcceptPage(load, 1, 2, 300, TestData.Night1).Value);
        Assert.Equal(SpolDatasetStatus.Loading, dataset.Status);
        Assert.Equal(300, dataset.ExpectedRows);
        Assert.Equal(TestData.Night1, dataset.LoadStartedAtUtc);
        dataset.RecordRowCount(200);

        Assert.False(dataset.AcceptPage(load, 2, 2, 300, TestData.Night1).Value);
        dataset.RecordRowCount(300);
        Assert.Equal(SpolDatasetStatus.Ready, dataset.Status);
        Assert.Equal(TestData.Night1, dataset.LastLoadedAtUtc);

        // A page sent again changes nothing about when it was loaded.
        Assert.False(dataset.AcceptPage(load, 2, 2, 300, TestData.Night1.AddMinutes(5)).Value);
        dataset.RecordRowCount(300);
        Assert.Equal(TestData.Night1, dataset.LastLoadedAtUtc);
    }

    [Fact]
    public void A_newer_load_takes_over_and_an_older_ones_pages_are_refused()
    {
        var dataset = NewDataset();
        var older = Guid.NewGuid();
        Assert.True(dataset.AcceptPage(older, 1, 3, 600, TestData.Night1).IsSuccess);
        dataset.RecordRowCount(200);

        Assert.True(dataset.AcceptPage(Guid.NewGuid(), 1, 1, 50, TestData.Night1).Value);
        dataset.RecordRowCount(50);

        var refused = dataset.AcceptPage(older, 2, 3, 600, TestData.Night1);
        Assert.Equal(ErrorCodes.DatasetLoadSuperseded, refused.Error!.Code);
        Assert.Equal(SpolDatasetStatus.Ready, dataset.Status);
    }

    [Theory]
    [InlineData(1, 2, 300, 1, 3, 300)]
    [InlineData(1, 2, 300, 2, 2, 301)]
    public void Every_page_of_a_load_must_agree_on_its_size(int page, int pages, int total, int laterPage, int laterPages, int laterTotal)
    {
        var dataset = NewDataset();
        var load = Guid.NewGuid();
        Assert.True(dataset.AcceptPage(load, page, pages, total, TestData.Night1).IsSuccess);

        Assert.Equal(ErrorCodes.ValidationFailed, dataset.AcceptPage(load, laterPage, laterPages, laterTotal, TestData.Night1).Error!.Code);
    }

    [Theory]
    [InlineData(0, 1, 1)]
    [InlineData(2, 1, 1)]
    [InlineData(1, 1, 0)]
    public void A_page_out_of_range_is_refused(int page, int pages, int total) =>
        Assert.True(NewDataset().AcceptPage(Guid.NewGuid(), page, pages, total, TestData.Night1).IsFailure);

    [Fact]
    public void A_load_over_200000_rows_is_too_large() =>
        Assert.Equal(ErrorCodes.DatasetTooLarge,
            NewDataset().AcceptPage(Guid.NewGuid(), 1, 1000, SpolDataset.MaxRows + 1, TestData.Night1).Error!.Code);
}

public sealed class SpolDatasetKeyTests
{
    [Fact]
    public void A_key_is_random_prefixed_and_only_its_hash_is_needed_to_check_it()
    {
        var (key, hash) = SpolDatasetKey.Generate();
        var (other, _) = SpolDatasetKey.Generate();

        Assert.StartsWith(SpolDatasetKey.Prefix, key, StringComparison.Ordinal);
        Assert.Equal(SpolDatasetKey.Prefix.Length + 43, key.Length);
        Assert.NotEqual(key, other);
        Assert.Equal(64, hash.Length);
        Assert.DoesNotContain(key[SpolDatasetKey.Prefix.Length..], hash, StringComparison.Ordinal);
        Assert.True(SpolDatasetKey.Matches(hash, key));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("cleanse_ds_wrong")]
    public void Anything_else_does_not_match(string? candidate)
    {
        var (_, hash) = SpolDatasetKey.Generate();

        Assert.False(SpolDatasetKey.Matches(hash, candidate));
    }

    [Fact]
    public void Nothing_matches_a_dataset_that_does_not_exist()
    {
        Assert.False(SpolDatasetKey.Matches(null, SpolDatasetKey.Prefix));
        Assert.False(SpolDatasetKey.Matches(null, SpolDatasetKey.Generate().Key));
    }

    [Fact]
    public void An_oversized_key_is_refused_without_hashing_it_as_given()
    {
        var huge = new string('a', 10_000);

        Assert.False(SpolDatasetKey.Matches(SpolDatasetKey.Hash(huge), huge));
    }
}

public sealed class SpolDatasetRowsTests
{
    private static SpolDatasetRowDto Row(int itemId) => new() { ItemId = itemId, Cph = "01/036/8000", RuleNo = "1" };

    [Fact]
    public void Rows_with_item_ids_and_offset_dates_are_accepted()
    {
        Assert.Null(SpolDatasetRows.Validate(
        [
            Row(1) with { DateReceived = "2026-04-01T00:00:00+01:00", Modified = "2026-04-01T15:16:00Z" },
            Row(2) with { DateComplete = "  " }
        ]));
    }

    [Fact]
    public void A_page_has_between_1_and_500_rows()
    {
        Assert.Equal(ErrorCodes.ValidationFailed, SpolDatasetRows.Validate([])!.Code);
        Assert.Equal(ErrorCodes.ValidationFailed, SpolDatasetRows.Validate(null)!.Code);
        Assert.Equal(ErrorCodes.DatasetTooLarge,
            SpolDatasetRows.Validate([.. Enumerable.Range(1, 501).Select(Row)])!.Code);
    }

    [Fact]
    public void Every_row_needs_a_unique_item_id()
    {
        Assert.Contains("Row 2", SpolDatasetRows.Validate([Row(1), Row(0)])!.Message, StringComparison.Ordinal);
        Assert.Contains("Row 1", SpolDatasetRows.Validate([null])!.Message, StringComparison.Ordinal);
        var twice = SpolDatasetRows.Validate([Row(4), Row(4)])!;
        Assert.Contains("Item 4", twice.Message, StringComparison.Ordinal);
        Assert.Equal("4", twice.Subject);
    }

    [Theory]
    [InlineData("2026-05-02T09:30:00")]
    [InlineData("02/05/2026")]
    [InlineData("tomorrow")]
    public void A_date_without_an_offset_names_the_item_and_property(string value)
    {
        var error = SpolDatasetRows.Validate([Row(9) with { LetterEmailSent = value }])!;

        Assert.Contains("Item 9", error.Message, StringComparison.Ordinal);
        Assert.Contains("letterEmailSent", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(value, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_value_has_at_most_64000_characters()
    {
        Assert.Null(SpolDatasetRows.Validate([Row(1) with { Notes = new string('x', SpolDatasetRows.MaxValueLength) }]));

        var error = SpolDatasetRows.Validate([Row(1) with { Notes = new string('x', SpolDatasetRows.MaxValueLength + 1) }])!;
        Assert.Contains("notes", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("2026-04-01T00:00:00+01:00", "2026-03-31T23:00:00.0000000+00:00")]
    [InlineData("2026-04-01T00:00:00Z", "2026-04-01T00:00:00.0000000+00:00")]
    [InlineData("2026-04-01T00:00:00.5-05:00", "2026-04-01T05:00:00.5000000+00:00")]
    public void Dates_are_read_as_the_moment_they_name(string value, string utc)
    {
        Assert.True(SpolDatasetRows.TryParseDate(value, out var parsed));
        Assert.Equal(utc, parsed.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void A_stored_row_keeps_what_was_sent_and_leaves_out_blanks()
    {
        var row = Row(12) with { Notes = "line one\nline two \u2019", AssignedName = "Alice Smith; Bob Jones" };

        var data = SpolDatasetRows.Serialize(row);

        Assert.DoesNotContain("null", data, StringComparison.Ordinal);
        Assert.Equal(row, SpolDatasetRows.Deserialize(data));
    }

    [Theory]
    [InlineData("itemId", true)]
    [InlineData("CPH", true)]
    [InlineData("letterEmailSent", true)]
    [InlineData("assignedTo", false)]
    public void Only_the_lists_columns_are_properties(string name, bool known) =>
        Assert.Equal(known, SpolDatasetRows.IsProperty(name));
}
