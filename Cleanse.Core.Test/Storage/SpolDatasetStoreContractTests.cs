using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain.Datasets;
using Cleanse.Core.Domain.Imports;
using Cleanse.Core.Test.Support;

namespace Cleanse.Core.Test.Storage;

/// <summary>
/// The behaviour every <see cref="ISpolDatasetStore"/> must exhibit, with the import jobs that
/// process datasets. A page is stored all or nothing, so a refused one never leaves part of itself
/// behind for the dataset to count.
/// </summary>
public abstract class SpolDatasetStoreContractTests
{
    private static readonly DateTimeOffset Night1 = TestData.Night1;

    protected abstract ISpolDatasetStore CreateStore();

    /// <summary>On the same database as <see cref="CreateStore"/>.</summary>
    protected abstract IImportJobStore CreateImportJobStore();

    /// <summary>A relational store's foreign key unlinks a deleted dataset's jobs.</summary>
    protected virtual bool DeletingADatasetUnlinksItsJobs => true;

    private static SpolDataset NewDataset(DateTimeOffset? at = null) =>
        SpolDataset.Create(Guid.NewGuid(), "November export", SpolDatasetKey.Hash(Guid.NewGuid().ToString()),
            TestData.Alice, at ?? Night1).Value;

    private static SpolDatasetItem Item(int itemId, string cph = "01/036/8000") =>
        new(itemId, SpolDatasetRows.Serialize(new SpolDatasetRowDto
        {
            ItemId = itemId, Cph = cph, Notes = "line one\nline two", DateReceived = "2026-04-01T00:00:00+01:00"
        }));

    private static async Task<IReadOnlyList<(int ItemId, string? Cph)>> RowsAsync(ISpolDatasetStore store, Guid id) =>
        [.. (await store.ReadItemsAsync(id)).Select(item => (item.ItemId, SpolDatasetRows.Deserialize(item.Data).Cph))];

    /// <summary>Takes a page through the dataset as the load command would.</summary>
    private static async Task<SpolDatasetPageOutcome> ApplyAsync(
        ISpolDatasetStore store, SpolDataset dataset, Guid load, int page, int pageCount, int totalRows,
        params SpolDatasetItem[] items)
    {
        var expectedVersion = dataset.Version;
        var clear = dataset.AcceptPage(load, page, pageCount, totalRows, Night1).Value;
        return await store.ApplyPageAsync(dataset, expectedVersion, clear, items);
    }

    [Fact]
    public async Task A_created_dataset_round_trips_every_field()
    {
        var store = CreateStore();
        var dataset = NewDataset();

        await store.CreateAsync(dataset);

        Assert.Equal(dataset.ToSnapshot(), (await store.GetAsync(dataset.Id))!.ToSnapshot());
    }

    [Fact]
    public async Task An_unknown_dataset_is_not_there_and_has_no_rows()
    {
        var store = CreateStore();

        Assert.Null(await store.GetAsync(Guid.NewGuid()));
        Assert.Empty(await store.ReadItemsAsync(Guid.NewGuid()));
        Assert.False(await store.DeleteAsync(Guid.NewGuid()));
        Assert.Equal(0, (await store.SearchAsync(PageRequest.Default)).TotalCount);
    }

    [Fact]
    public async Task Datasets_are_listed_newest_first_a_page_at_a_time()
    {
        var store = CreateStore();
        var datasets = Enumerable.Range(0, 3).Select(i => NewDataset(Night1.AddMinutes(i))).ToList();
        foreach (var dataset in datasets)
        {
            await store.CreateAsync(dataset);
        }

        var first = await store.SearchAsync(new PageRequest(1, 2));
        var second = await store.SearchAsync(new PageRequest(2, 2));

        Assert.Equal(3, first.TotalCount);
        Assert.Equal([datasets[2].Id, datasets[1].Id], first.Items.Select(d => d.Id));
        Assert.Equal([datasets[0].Id], second.Items.Select(d => d.Id));
    }

    [Fact]
    public async Task A_save_needs_the_stored_version()
    {
        var store = CreateStore();
        var dataset = NewDataset();
        await store.CreateAsync(dataset);
        Assert.True(dataset.Rename("SPOL 27 Nov", Night1.AddHours(1)).IsSuccess);

        Assert.False(await store.SaveAsync(dataset, expectedVersion: 7));
        Assert.Equal("November export", (await store.GetAsync(dataset.Id))!.Name);

        Assert.True(await store.SaveAsync(dataset, expectedVersion: 1));
        Assert.Equal(dataset.ToSnapshot(), (await store.GetAsync(dataset.Id))!.ToSnapshot());
    }

    [Fact]
    public async Task Pages_are_stored_by_item_id_and_the_dataset_is_ready_when_the_load_is_complete()
    {
        var store = CreateStore();
        var dataset = NewDataset();
        await store.CreateAsync(dataset);
        var load = Guid.NewGuid();

        Assert.Equal(SpolDatasetPageOutcome.Stored, await ApplyAsync(store, dataset, load, 1, 2, 3, Item(2), Item(1)));
        Assert.Equal(2, dataset.RowCount);
        Assert.Equal(SpolDatasetStatus.Loading, dataset.Status);
        Assert.Equal(dataset.ToSnapshot(), (await store.GetAsync(dataset.Id))!.ToSnapshot());

        // A row sent again replaces the one with its item ID.
        Assert.Equal(SpolDatasetPageOutcome.Stored,
            await ApplyAsync(store, dataset, load, 2, 2, 3, Item(2, "01/036/9999"), Item(3)));
        Assert.Equal(3, dataset.RowCount);
        Assert.Equal(SpolDatasetStatus.Ready, dataset.Status);
        Assert.Equal(dataset.ToSnapshot(), (await store.GetAsync(dataset.Id))!.ToSnapshot());
        Assert.Equal([(1, "01/036/8000"), (2, "01/036/9999"), (3, "01/036/8000")], await RowsAsync(store, dataset.Id));

        var stored = (await store.ReadItemsAsync(dataset.Id))[0];
        Assert.Equal("line one\nline two", SpolDatasetRows.Deserialize(stored.Data).Notes);
    }

    [Fact]
    public async Task A_new_loads_first_page_replaces_the_rows()
    {
        var store = CreateStore();
        var dataset = NewDataset();
        await store.CreateAsync(dataset);
        Assert.Equal(SpolDatasetPageOutcome.Stored, await ApplyAsync(store, dataset, Guid.NewGuid(), 1, 1, 2, Item(1), Item(2)));

        Assert.Equal(SpolDatasetPageOutcome.Stored, await ApplyAsync(store, dataset, Guid.NewGuid(), 1, 2, 4, Item(9)));

        Assert.Equal([(9, "01/036/8000")], await RowsAsync(store, dataset.Id));
        Assert.Equal(1, (await store.GetAsync(dataset.Id))!.RowCount);
    }

    [Fact]
    public async Task A_page_against_a_stale_version_stores_nothing()
    {
        var store = CreateStore();
        var dataset = NewDataset();
        await store.CreateAsync(dataset);
        var stale = (await store.GetAsync(dataset.Id))!;
        Assert.Equal(SpolDatasetPageOutcome.Stored, await ApplyAsync(store, dataset, Guid.NewGuid(), 1, 1, 1, Item(1)));
        var before = (await store.GetAsync(dataset.Id))!.ToSnapshot();

        Assert.Equal(SpolDatasetPageOutcome.VersionConflict, await ApplyAsync(store, stale, Guid.NewGuid(), 1, 1, 1, Item(5)));

        Assert.Equal(before, (await store.GetAsync(dataset.Id))!.ToSnapshot());
        Assert.Equal([(1, "01/036/8000")], await RowsAsync(store, dataset.Id));
    }

    [Fact]
    public async Task A_page_that_would_pass_the_loads_total_stores_nothing()
    {
        var store = CreateStore();
        var dataset = NewDataset();
        await store.CreateAsync(dataset);
        var load = Guid.NewGuid();
        Assert.Equal(SpolDatasetPageOutcome.Stored, await ApplyAsync(store, dataset, load, 1, 2, 2, Item(1), Item(2)));
        var before = (await store.GetAsync(dataset.Id))!;

        Assert.Equal(SpolDatasetPageOutcome.TooManyRows, await ApplyAsync(store, before, load, 2, 2, 2, Item(3)));

        Assert.Equal(dataset.ToSnapshot(), (await store.GetAsync(dataset.Id))!.ToSnapshot());
        Assert.Equal([1, 2], (await RowsAsync(store, dataset.Id)).Select(row => row.ItemId));

        // Nor does a new load's first page clear anything when it is refused.
        var reload = (await store.GetAsync(dataset.Id))!;
        Assert.Equal(SpolDatasetPageOutcome.TooManyRows,
            await ApplyAsync(store, reload, Guid.NewGuid(), 1, 1, 1, Item(7), Item(8)));
        Assert.Equal([1, 2], (await RowsAsync(store, dataset.Id)).Select(row => row.ItemId));
    }

    [Fact]
    public async Task Deleting_a_dataset_takes_its_rows_and_keeps_its_import_jobs()
    {
        var store = CreateStore();
        var imports = CreateImportJobStore();
        var dataset = NewDataset();
        await store.CreateAsync(dataset);
        Assert.Equal(SpolDatasetPageOutcome.Stored, await ApplyAsync(store, dataset, Guid.NewGuid(), 1, 1, 1, Item(1)));
        var job = ImportJob.Create(Guid.NewGuid(), TestData.Group, dataset.Name, dataset.Id, "Alice Brown", 1, Night1);
        Assert.True(await imports.TryCreateAsync(job));

        Assert.True(await store.DeleteAsync(dataset.Id));

        Assert.Null(await store.GetAsync(dataset.Id));
        Assert.Empty(await store.ReadItemsAsync(dataset.Id));
        var kept = (await imports.GetAsync(job.Id))!;
        Assert.Equal(DeletingADatasetUnlinksItsJobs ? null : dataset.Id, kept.DatasetId);
        Assert.False(await store.DeleteAsync(dataset.Id));
    }

    [Fact]
    public async Task A_datasets_import_jobs_are_found_by_the_dataset()
    {
        var store = CreateStore();
        var imports = CreateImportJobStore();
        var dataset = NewDataset();
        var other = NewDataset();
        await store.CreateAsync(dataset);
        await store.CreateAsync(other);
        var job = ImportJob.Create(Guid.NewGuid(), TestData.Group, dataset.Name, dataset.Id, "Alice Brown", 5, Night1);
        Assert.True(await imports.TryCreateAsync(job));
        Assert.True(await imports.TryCreateAsync(
            ImportJob.Create(Guid.NewGuid(), "elsewhere", other.Name, other.Id, "Alice Brown", 1, Night1)));

        Assert.Equal(job.ToSnapshot(), (await imports.GetAsync(job.Id))!.ToSnapshot());
        Assert.True(await imports.HasActiveForDatasetAsync(dataset.Id));
        Assert.Equal(job.Id, Assert.Single((await imports.SearchAsync(null, PageRequest.Default, dataset.Id)).Items).Id);
        Assert.Empty((await imports.SearchAsync("elsewhere", PageRequest.Default, dataset.Id)).Items);

        var leased = (await imports.LeaseNextPendingAsync(Night1))!;
        Assert.True(await imports.HasActiveForDatasetAsync(leased.DatasetId!.Value));
        Assert.True(leased.RecordProgress(ImportJobCounters.Empty with { UsersCreated = 2 }, Night1).IsSuccess);
        Assert.True(leased.Complete(Night1.AddMinutes(1)).IsSuccess);
        await imports.SaveAsync(leased);

        Assert.Equal(leased.ToSnapshot(), (await imports.GetAsync(leased.Id))!.ToSnapshot());
        Assert.Equal(2, (await imports.GetAsync(leased.Id))!.Counters.UsersCreated);
        Assert.False(await imports.HasActiveForDatasetAsync(leased.DatasetId.Value));
        Assert.False(await imports.HasActiveForDatasetAsync(Guid.NewGuid()));
    }
}
