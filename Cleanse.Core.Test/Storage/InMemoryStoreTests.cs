using Cleanse.Core.Abstractions;
using Cleanse.Storage.InMemory;

namespace Cleanse.Core.Test.Storage;

public sealed class InMemoryIssueStoreTests : IssueStoreContractTests
{
    private readonly InMemoryHoldingStore _holdings = new();

    protected override IIssueStore CreateStore() => new InMemoryIssueStore(_holdings);

    protected override IHoldingStore CreateHoldingStore() => _holdings;

    protected override ICodedValueStore CreateCodedValueStore() => new InMemoryCodedValueStore();
}

public sealed class InMemoryAuditStoreTests : AuditStoreContractTests
{
    protected override IAuditStore CreateStore() => new InMemoryAuditStore();
}

public sealed class InMemoryIssueHistoryStoreTests : IssueHistoryStoreContractTests
{
    protected override IIssueHistoryStore CreateStore() => new InMemoryIssueHistoryStore();
}

public sealed class InMemoryRuleRunStoreTests : RuleRunStoreContractTests
{
    protected override IRuleRunStore CreateStore() => new InMemoryRuleRunStore();
}

public sealed class InMemoryImportJobStoreTests : ImportJobStoreContractTests
{
    protected override IImportJobStore CreateStore() => new InMemoryImportJobStore();
}

public sealed class InMemoryHoldingStoreTests : HoldingStoreContractTests
{
    private readonly InMemoryHoldingStore _store = new();

    protected override IHoldingStore CreateStore() => _store;
}

public sealed class InMemoryHoldingActivityStoreTests : HoldingActivityStoreContractTests
{
    private readonly InMemoryHoldingActivityStore _store = new();
    private readonly InMemoryCodedValueStore _codedValues = new();

    protected override IHoldingActivityStore CreateStore() => _store;

    protected override ICodedValueStore CreateCodedValueStore() => _codedValues;
}

public sealed class InMemoryCodedValueStoreTests : CodedValueStoreContractTests
{
    private readonly InMemoryCodedValueStore _store = new();

    protected override ICodedValueStore CreateStore() => _store;
}

public sealed class InMemorySettingsStoreTests : SettingsStoreContractTests
{
    private readonly InMemorySettingsStore _store = new();

    protected override ISettingsStore CreateStore() => _store;
}
