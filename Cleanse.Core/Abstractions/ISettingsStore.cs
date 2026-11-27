using Cleanse.Core.Domain;

namespace Cleanse.Core.Abstractions;

/// <summary>The service-wide settings. A reset keeps them, as it does users and the admin lists.</summary>
public interface ISettingsStore
{
    /// <summary>Null until an admin first saves them.</summary>
    Task<ServiceSettings?> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(ServiceSettings settings, CancellationToken cancellationToken = default);
}
