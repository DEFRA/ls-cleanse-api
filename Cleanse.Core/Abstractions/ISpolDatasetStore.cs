using Cleanse.Core.Domain.Datasets;

namespace Cleanse.Core.Abstractions;

/// <summary>SPOL datasets and their rows. A reset keeps them, as it does users.</summary>
public interface ISpolDatasetStore
{
    Task<SpolDataset?> GetAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Newest first.</summary>
    Task<PagedResult<SpolDataset>> SearchAsync(PageRequest page, CancellationToken cancellationToken = default);

    Task CreateAsync(SpolDataset dataset, CancellationToken cancellationToken = default);

    /// <summary>
    /// Saves a change to the dataset itself, such as a rename. Returns false, having changed
    /// nothing, when the stored version is not <paramref name="expectedVersion"/>.
    /// </summary>
    Task<bool> SaveAsync(SpolDataset dataset, int expectedVersion, CancellationToken cancellationToken = default);

    /// <summary>Deletes the dataset with its rows. Returns false when there was no such dataset.</summary>
    Task<bool> DeleteAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stores one page of a load, all or nothing: clears the dataset's rows first when
    /// <paramref name="clearRows"/> is set, stores each item by its item ID, replacing one already
    /// there, then gives the dataset its new row count through
    /// <see cref="SpolDataset.RecordRowCount"/> and saves it. Nothing changes when the stored
    /// version is not <paramref name="expectedVersion"/>, or when the rows would go past the
    /// load's total.
    /// </summary>
    Task<SpolDatasetPageOutcome> ApplyPageAsync(
        SpolDataset dataset,
        int expectedVersion,
        bool clearRows,
        IReadOnlyCollection<SpolDatasetItem> items,
        CancellationToken cancellationToken = default);

    /// <summary>Every row, in item ID order.</summary>
    Task<IReadOnlyList<SpolDatasetItem>> ReadItemsAsync(Guid id, CancellationToken cancellationToken = default);
}
