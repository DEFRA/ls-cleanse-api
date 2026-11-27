namespace Cleanse.Core;

public sealed class CleanseCoreOptions
{
    /// <summary>How many detections are read, merged and written per round trip.</summary>
    public int DetectionBatchSize { get; set; } = 500;

    public int SuppressionExpiryBatchSize { get; set; } = 500;

    /// <summary>Caps a single expiry sweep so a pathological backlog cannot spin forever.</summary>
    public int MaxSuppressionExpiryBatches { get; set; } = 100;

    /// <summary>The dashboard's summary is rebuilt at least this often, even when nothing has changed.</summary>
    public TimeSpan MiRebuildAfter { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Figures no refresh has confirmed for this long are reported as stale.</summary>
    public TimeSpan MiStaleAfter { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>An In progress holding nobody has worked on for this many days counts as stalled.</summary>
    public int MiStalledAfterDays { get; set; } = 14;

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(DetectionBatchSize, 1, nameof(DetectionBatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(SuppressionExpiryBatchSize, 1, nameof(SuppressionExpiryBatchSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(MaxSuppressionExpiryBatches, 1, nameof(MaxSuppressionExpiryBatches));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MiRebuildAfter, TimeSpan.Zero, nameof(MiRebuildAfter));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(MiStaleAfter, TimeSpan.Zero, nameof(MiStaleAfter));
        ArgumentOutOfRangeException.ThrowIfLessThan(MiStalledAfterDays, 1, nameof(MiStalledAfterDays));
    }
}
