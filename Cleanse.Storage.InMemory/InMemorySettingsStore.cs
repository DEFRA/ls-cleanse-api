using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;

namespace Cleanse.Storage.InMemory;

/// <summary>The service-wide settings. A reset leaves them alone, as it does users.</summary>
public sealed class InMemorySettingsStore : ISettingsStore
{
    private ServiceSettings? _settings;

    public Task<ServiceSettings?> GetAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Volatile.Read(ref _settings));
    }

    public Task SaveAsync(ServiceSettings settings, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        cancellationToken.ThrowIfCancellationRequested();
        Volatile.Write(ref _settings, settings);
        return Task.CompletedTask;
    }
}
