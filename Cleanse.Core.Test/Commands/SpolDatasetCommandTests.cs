using Cleanse.Core.Commands.Datasets;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain.Datasets;
using Cleanse.Core.Domain.Imports;
using Cleanse.Core.Queries.Datasets;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Commands;

public sealed class SpolDatasetCommandTests : IDisposable
{
    private readonly CleanseHarness _harness = new();

    private static SpolDatasetRowDto Row(int itemId) => new() { ItemId = itemId, Cph = $"01/036/{itemId:D4}", RuleNo = "1" };

    private async Task<(Guid Id, string Key)> CreateAsync(string? name = null)
    {
        var created = await _harness.SendAsync(new CreateSpolDatasetCommand(name, TestData.Alice));
        return (created.Dataset.Id, created.Key);
    }

    private Task<Result<SpolDatasetPageResultDto>> LoadAsync(
        Guid id, string? key, Guid loadId, int page, int pageCount, int totalRows, params SpolDatasetRowDto[] rows) =>
        _harness.TrySendAsync(new LoadSpolDatasetPageCommand(id, key, loadId, page, pageCount, totalRows, rows));

    private async Task<Guid> ReadyAsync(params SpolDatasetRowDto[] rows) => (await ReadyWithKeyAsync(rows)).Id;

    private async Task<(Guid Id, string Key)> ReadyWithKeyAsync(params SpolDatasetRowDto[] rows)
    {
        var (id, key) = await CreateAsync();
        Assert.Equal("ready", (await LoadAsync(id, key, Guid.NewGuid(), 1, 1, rows.Length, rows)).Value.Status);
        return (id, key);
    }

    [Fact]
    public async Task Creating_keeps_only_the_keys_hash()
    {
        var (id, key) = await CreateAsync("November export");

        var stored = (await _harness.Datasets.GetAsync(id))!;
        Assert.Equal(SpolDatasetKey.Hash(key), stored.KeyHash);
        Assert.Equal(TestData.Alice.DisplayName, (await _harness.QueryAsync(new GetSpolDatasetQuery(id))).CreatedBy);
    }

    [Fact]
    public async Task A_page_is_stored_only_with_the_right_key()
    {
        var (id, key) = await CreateAsync();

        Assert.Equal(ErrorCodes.DatasetUnauthorised, (await LoadAsync(id, "cleanse_ds_wrong", Guid.NewGuid(), 1, 1, 1, Row(1))).Error!.Code);
        Assert.Equal(ErrorCodes.DatasetUnauthorised, (await LoadAsync(id, null, Guid.NewGuid(), 1, 1, 1, Row(1))).Error!.Code);
        Assert.Equal(ErrorCodes.DatasetUnauthorised, (await LoadAsync(Guid.NewGuid(), key, Guid.NewGuid(), 1, 1, 1, Row(1))).Error!.Code);
        Assert.Empty(await _harness.Datasets.ReadItemsAsync(id));

        var stored = await LoadAsync(id, key, Guid.NewGuid(), 1, 1, 1, Row(1));
        Assert.Equal(1, stored.Value.RowCount);
    }

    [Fact]
    public async Task The_key_is_checked_before_anything_about_the_page()
    {
        var (id, _) = await CreateAsync();

        Assert.Equal(ErrorCodes.DatasetUnauthorised, (await LoadAsync(id, "wrong", Guid.Empty, 0, 0, 0)).Error!.Code);
    }

    [Fact]
    public async Task A_page_with_more_rows_than_the_load_is_refused()
    {
        var (id, key) = await CreateAsync();

        Assert.Equal(ErrorCodes.ValidationFailed, (await LoadAsync(id, key, Guid.NewGuid(), 1, 1, 1, Row(1), Row(2))).Error!.Code);

        // Nor can pages that repeat no item take the dataset past the load's total.
        var load = Guid.NewGuid();
        Assert.True((await LoadAsync(id, key, load, 1, 2, 2, Row(1), Row(2))).IsSuccess);
        Assert.Equal(ErrorCodes.ValidationFailed, (await LoadAsync(id, key, load, 2, 2, 2, Row(3))).Error!.Code);
        Assert.Equal([1, 2], (await _harness.Datasets.ReadItemsAsync(id)).Select(item => item.ItemId));
    }

    [Fact]
    public async Task Nothing_is_loaded_and_nothing_deleted_while_the_dataset_is_being_processed()
    {
        var (id, key) = await ReadyWithKeyAsync(Row(1));
        await _harness.SendAsync(new ProcessSpolDatasetCommand(id, "cts-sam", TestData.Alice));

        Assert.Equal(ErrorCodes.DatasetProcessing, (await LoadAsync(id, key, Guid.NewGuid(), 1, 1, 1, Row(2))).Error!.Code);
        Assert.Equal(ErrorCodes.DatasetProcessing, (await _harness.TrySendAsync(new DeleteSpolDatasetCommand(id))).Error!.Code);
        Assert.Equal([1], (await _harness.Datasets.ReadItemsAsync(id)).Select(item => item.ItemId));
    }

    [Fact]
    public async Task Processing_starts_an_import_job_for_the_datasets_rows()
    {
        var id = await ReadyAsync(Row(1), Row(2), Row(3));

        var job = await _harness.SendAsync(new ProcessSpolDatasetCommand(id, " cts-sam ", TestData.Alice));

        Assert.Equal("pending", job.Status);
        Assert.Equal("cts-sam", job.GroupId);
        Assert.Equal(id, job.DatasetId);
        Assert.Equal(ImportJobDto.DatasetSource, job.Source);
        Assert.Equal(3, job.TotalRows);
        Assert.Equal(TestData.Alice.DisplayName, job.RequestedBy);
        var stored = (await _harness.Imports.GetAsync(job.Id))!;
        Assert.Null(stored.BlobKey);

        var dataset = await _harness.QueryAsync(new GetSpolDatasetQuery(id));
        Assert.Equal(job.Id, dataset.LastProcessing!.ImportJobId);
        Assert.Equal("pending", dataset.LastProcessing.Status);
    }

    [Fact]
    public void An_earlier_uploads_job_says_it_came_from_a_file() =>
        Assert.Equal(ImportJobDto.FileSource, TestData.NewUploadJob().ToDto().Source);

    [Fact]
    public async Task A_group_processes_one_dataset_at_a_time()
    {
        var first = await ReadyAsync(Row(1));
        var second = await ReadyAsync(Row(2));
        await _harness.SendAsync(new ProcessSpolDatasetCommand(first, "cts-sam", TestData.Alice));

        var refused = await _harness.TrySendAsync(new ProcessSpolDatasetCommand(second, "cts-sam", TestData.Alice));

        Assert.Equal(ErrorCodes.ImportAlreadyInProgress, refused.Error!.Code);
        Assert.True((await _harness.TrySendAsync(new ProcessSpolDatasetCommand(second, "elsewhere", TestData.Alice))).IsSuccess);
    }

    [Fact]
    public async Task Only_a_ready_dataset_is_processed()
    {
        var (id, key) = await CreateAsync();
        Assert.Equal(ErrorCodes.DatasetNotReady,
            (await _harness.TrySendAsync(new ProcessSpolDatasetCommand(id, "cts-sam", TestData.Alice))).Error!.Code);

        await LoadAsync(id, key, Guid.NewGuid(), 1, 2, 2, Row(1));
        Assert.Equal(ErrorCodes.DatasetNotReady,
            (await _harness.TrySendAsync(new ProcessSpolDatasetCommand(id, "cts-sam", TestData.Alice))).Error!.Code);
        Assert.Equal(ErrorCodes.ValidationFailed,
            (await _harness.TrySendAsync(new ProcessSpolDatasetCommand(id, " ", TestData.Alice))).Error!.Code);
    }

    [Fact]
    public async Task A_rename_needs_the_version_that_was_read()
    {
        var (id, key) = await CreateAsync("November export");
        await LoadAsync(id, key, Guid.NewGuid(), 1, 1, 1, Row(1));

        Assert.Equal(ErrorCodes.DatasetConcurrencyConflict,
            (await _harness.TrySendAsync(new RenameSpolDatasetCommand(id, "Stale", 1))).Error!.Code);

        var current = (await _harness.Datasets.GetAsync(id))!.Version;
        var renamed = await _harness.SendAsync(new RenameSpolDatasetCommand(id, "SPOL 27 Nov", current));
        Assert.Equal("SPOL 27 Nov", renamed.Name);
        Assert.Equal(current + 1, renamed.Version);
        Assert.Equal(1, renamed.RowCount);
    }

    [Fact]
    public async Task Deleting_takes_the_rows_and_keeps_the_import_jobs()
    {
        var id = await ReadyAsync(Row(1));
        var job = await _harness.SendAsync(new ProcessSpolDatasetCommand(id, "cts-sam", TestData.Alice));
        var leased = (await _harness.Imports.LeaseNextPendingAsync(TestData.Night1))!;
        Assert.True(leased.Complete(TestData.Night1).IsSuccess);
        await _harness.Imports.SaveAsync(leased);

        Assert.Equal(id, await _harness.SendAsync(new DeleteSpolDatasetCommand(id)));

        Assert.Null(await _harness.Datasets.GetAsync(id));
        Assert.Empty(await _harness.Datasets.ReadItemsAsync(id));
        Assert.NotNull(await _harness.Imports.GetAsync(job.Id));
        Assert.Equal(ErrorCodes.DatasetNotFound, (await _harness.TrySendAsync(new DeleteSpolDatasetCommand(id))).Error!.Code);
    }

    [Fact]
    public async Task Datasets_are_listed_newest_first_with_their_last_processing()
    {
        var older = await ReadyAsync(Row(1));
        _harness.Clock.Advance(TimeSpan.FromMinutes(1));
        var (newer, _) = await CreateAsync();
        var job = await _harness.SendAsync(new ProcessSpolDatasetCommand(older, "cts-sam", TestData.Alice));

        var listed = await _harness.QueryAsync(new SearchSpolDatasetsQuery());

        Assert.Equal([newer, older], listed.Items.Select(d => d.Id));
        Assert.Null(listed.Items[0].LastProcessing);
        Assert.Equal(job.Id, listed.Items[1].LastProcessing!.ImportJobId);
    }

    public void Dispose() => _harness.Dispose();
}
