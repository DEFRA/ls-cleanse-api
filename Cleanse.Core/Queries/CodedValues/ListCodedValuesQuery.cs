using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Queries.CodedValues;

/// <summary>An admin-maintained list, ordered by name. Pickers ask for the active entries only.</summary>
public sealed record ListCodedValuesQuery(CodedList List, bool ActiveOnly = false) : IQuery<IReadOnlyList<CodedValueDto>>;

internal sealed class ListCodedValuesQueryHandler(ICodedValueStore store)
    : IQueryHandler<ListCodedValuesQuery, IReadOnlyList<CodedValueDto>>
{
    public async Task<Result<IReadOnlyList<CodedValueDto>>> HandleAsync(
        ListCodedValuesQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var values = await store.ListAsync(query.List, cancellationToken);
        return Result<IReadOnlyList<CodedValueDto>>.Success(
            [.. values.Where(value => !query.ActiveOnly || value.IsActive).Select(value => value.ToDto())]);
    }
}
