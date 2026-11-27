using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Cleanse.Core.Domain.Mi;
using Cleanse.Core.Test.Storage;
using Cleanse.Core.Test.Support;
using Cleanse.Storage.Postgres.Schema;
using Cleanse.Storage.Postgres.Stores;
using Cleanse.Storage.Postgres.Test.Support;
using Microsoft.EntityFrameworkCore;

namespace Cleanse.Storage.Postgres.Test;

[Collection(PostgresCollection.Name)]
public sealed class PostgresIssueStoreTests(PostgresFixture fixture)
    : IssueStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IIssueStore CreateStore() =>
        // Threshold low enough that the batch tests exercise COPY rather than INSERT.
        new PostgresIssueStore(_harness.NewContext(), CleansePostgresOptions.ForExistingDataSource(3));

    protected override IHoldingStore CreateHoldingStore() => new PostgresHoldingStore(_harness.NewContext());

    protected override ICodedValueStore CreateCodedValueStore() => new PostgresCodedValueStore(_harness.NewContext());

    /// <summary>
    /// Guards the index the grouped search relies on: it has to exist, and its expression has to be
    /// the one the grouping SQL uses, or the planner will not pick it.
    /// </summary>
    [Fact]
    public async Task The_cph_group_index_serves_the_grouped_search_expression()
    {
        var db = _harness.NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off");

        var plan = await db.Database.SqlQueryRaw<string>(
            "EXPLAIN SELECT id FROM issues WHERE group_id = 'g' " +
            "AND COALESCE(context ->> 'cph', '') = ANY(ARRAY['01/001/0001']) ").ToListAsync();

        Assert.Contains(plan, line => line.Contains("ix_issues_group_cph", StringComparison.Ordinal));
    }

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresAuditStoreTests(PostgresFixture fixture)
    : AuditStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IAuditStore CreateStore() => new PostgresAuditStore(_harness.NewContext());

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresIssueHistoryStoreTests(PostgresFixture fixture)
    : IssueHistoryStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IIssueHistoryStore CreateStore() =>
        new PostgresIssueHistoryStore(_harness.NewContext());

    /// <summary>History rows carry a foreign key, so the parent has to exist first.</summary>
    protected override async Task EnsureIssueExistsAsync(IssueId issueId)
    {
        var store = new PostgresIssueStore(
            _harness.NewContext(), CleansePostgresOptions.ForExistingDataSource());

        var detection = new IssueDetection(issueId, TestData.Rule2);
        await store.SaveAsync(
            Issue.FromDetection(detection, TestData.Group, "run-1", TestData.Night1),
            expectedVersion: null);
    }

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresRuleRunStoreTests(PostgresFixture fixture)
    : RuleRunStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IRuleRunStore CreateStore() => new PostgresRuleRunStore(_harness.NewContext());

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresImportJobStoreTests(PostgresFixture fixture)
    : ImportJobStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IImportJobStore CreateStore() => new PostgresImportJobStore(_harness.NewContext());

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresHoldingStoreTests(PostgresFixture fixture)
    : HoldingStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IHoldingStore CreateStore() => new PostgresHoldingStore(_harness.NewContext());

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresHoldingActivityStoreTests(PostgresFixture fixture)
    : HoldingActivityStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IHoldingActivityStore CreateStore() => new PostgresHoldingActivityStore(_harness.NewContext());

    protected override ICodedValueStore CreateCodedValueStore() => new PostgresCodedValueStore(_harness.NewContext());

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresCodedValueStoreTests(PostgresFixture fixture)
    : CodedValueStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override ICodedValueStore CreateStore() => new PostgresCodedValueStore(_harness.NewContext());

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresSettingsStoreTests(PostgresFixture fixture)
    : SettingsStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override ISettingsStore CreateStore() => new PostgresSettingsStore(_harness.NewContext());

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresSpolDatasetStoreTests(PostgresFixture fixture)
    : SpolDatasetStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override ISpolDatasetStore CreateStore() => new PostgresSpolDatasetStore(_harness.NewContext());

    protected override IImportJobStore CreateImportJobStore() => new PostgresImportJobStore(_harness.NewContext());

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

[Collection(PostgresCollection.Name)]
public sealed class PostgresMiStoreTests(PostgresFixture fixture)
    : MiStoreContractTests, IAsyncLifetime
{
    private readonly PostgresStoreHarness _harness = new(fixture);

    protected override IMiStore CreateStore() => new PostgresMiStore(_harness.NewContext());

    protected override IIssueStore CreateIssueStore() =>
        new PostgresIssueStore(_harness.NewContext(), CleansePostgresOptions.ForExistingDataSource());

    protected override IAuditStore CreateAuditStore() => new PostgresAuditStore(_harness.NewContext());

    protected override IHoldingStore CreateHoldingStore() => new PostgresHoldingStore(_harness.NewContext());

    protected override IHoldingActivityStore CreateActivityStore() => new PostgresHoldingActivityStore(_harness.NewContext());

    protected override ICodedValueStore CreateCodedValueStore() => new PostgresCodedValueStore(_harness.NewContext());

    protected override IRuleRunStore CreateRuleRunStore() => new PostgresRuleRunStore(_harness.NewContext());

    /// <summary>A second instance replacing at the same moment writes nothing, rather than doubling the rows.</summary>
    [Fact]
    public async Task A_replace_while_another_is_in_progress_writes_nothing()
    {
        var holder = _harness.NewContext();
        await using var transaction = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlRawAsync(
            "SELECT pg_advisory_xact_lock(hashtextextended('cleanse.mi:' || {0}, 0))", TestData.Group);

        var summary = new MiSummary(
            new MiRefreshRecord(TestData.Group, TestData.Night1, TestData.Night1, "f", 1, 0, null, 0, 0, 0),
            [], [], [], []);

        Assert.False(await CreateStore().ReplaceAsync(summary));
        await transaction.RollbackAsync();

        Assert.Null(await CreateStore().GetRefreshAsync(TestData.Group));
        Assert.True(await CreateStore().ReplaceAsync(summary));
        Assert.NotNull(await CreateStore().GetRefreshAsync(TestData.Group));
    }

    /// <summary>Guards the index the refresh reads the audit log through, as the grouped search's is guarded.</summary>
    [Fact]
    public async Task The_refresh_reads_the_audit_log_through_its_partial_index()
    {
        var db = _harness.NewContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SET LOCAL enable_seqscan = off");

        // The filter is a constant from the provider, not input, so there is nothing to inject.
        var explain = "EXPLAIN SELECT count(*), max(sequence) FROM audit_entries WHERE " + Db.MiAuditFilter;
        var plan = await db.Database.SqlQueryRaw<string>(explain).ToListAsync();

        Assert.Contains(plan, line => line.Contains("ix_audit_entries_mi", StringComparison.Ordinal));
    }

    public ValueTask InitializeAsync() => _harness.InitializeAsync();

    public ValueTask DisposeAsync() => _harness.DisposeAsync();
}

/// <summary>
/// Composition rather than inheritance, because each contract suite is already a base class.
/// </summary>
internal sealed class PostgresStoreHarness(PostgresFixture fixture)
{
    private readonly List<CleanseDbContext> _contexts = [];

    public CleanseDbContext NewContext()
    {
        Assert.SkipWhen(!fixture.IsAvailable, fixture.SkipReason ?? "Docker is not available.");
        var context = fixture.CreateContext();
        _contexts.Add(context);
        return context;
    }

    public async ValueTask InitializeAsync() => await fixture.ResetAsync();

    public async ValueTask DisposeAsync()
    {
        foreach (var context in _contexts)
        {
            await context.DisposeAsync();
        }

        _contexts.Clear();
    }
}
