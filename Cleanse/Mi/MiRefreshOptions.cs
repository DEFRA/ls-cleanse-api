namespace Cleanse.Api.Mi;

/// <summary>The <c>Mi</c> configuration section: how the dashboard's figures are kept up to date.</summary>
public sealed class MiRefreshOptions
{
    /// <summary>Whether this instance refreshes the figures. With it off, the dashboard shows what is there.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// How often to check for changes, and rebuild if there are any. Each check is a few cheap
    /// counts; a rebuild reads the group's history, so this also bounds how often that happens.
    /// The figures are at most this, plus the time a rebuild takes, behind.
    /// </summary>
    public TimeSpan CheckInterval { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// How long after startup the first check waits, unless a rebuild is asked for first. Keeps
    /// short-lived hosts, such as the build-time OpenAPI pass, from reading the history.
    /// </summary>
    public TimeSpan StartupDelay { get; set; } = TimeSpan.FromSeconds(30);

    internal void Validate()
    {
        if (CheckInterval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(nameof(CheckInterval), CheckInterval, "Must be at least a second.");
        }

        if (StartupDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(StartupDelay), StartupDelay, "Must not be negative.");
        }
    }
}
