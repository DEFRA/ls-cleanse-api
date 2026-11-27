using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Queries.Settings;

public sealed record GetSettingsQuery : IQuery<SettingsDto>;

internal sealed class GetSettingsQueryHandler(ISettingsStore store) : IQueryHandler<GetSettingsQuery, SettingsDto>
{
    public async Task<Result<SettingsDto>> HandleAsync(
        GetSettingsQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return (await store.GetAsync(cancellationToken) ?? ServiceSettings.Default).ToDto();
    }
}
