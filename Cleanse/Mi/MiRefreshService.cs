using Cleanse.Core.Abstractions;
using Cleanse.Core.Commands.Mi;
using Cleanse.Rules;

namespace Cleanse.Api.Mi;

/// <summary>
/// Wakes the refresh early, for a rebuild an admin asked for, and says whether one is under way.
/// Per instance: another instance's status does not know this one is rebuilding.
/// </summary>
public sealed class MiRefreshSignal
{
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _requested;
    private int _rebuilding;

    public bool Building => Volatile.Read(ref _requested) == 1 || Volatile.Read(ref _rebuilding) == 1;

    public void RequestRebuild()
    {
        Interlocked.Exchange(ref _requested, 1);
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // Already woken; the rebuild is asked for once however often the button is pressed.
        }
    }

    /// <summary>Waits for <paramref name="delay"/>, or less if a rebuild is asked for meanwhile.</summary>
    internal Task WaitAsync(TimeSpan delay, CancellationToken cancellationToken) =>
        _wake.WaitAsync(delay, cancellationToken);

    /// <summary>Takes the request, if there is one, so a rebuild asked for during this one runs again.</summary>
    internal bool Begin()
    {
        var force = Interlocked.Exchange(ref _requested, 0) == 1;
        Volatile.Write(ref _rebuilding, force ? 1 : 0);
        return force;
    }

    internal void End() => Volatile.Write(ref _rebuilding, 0);
}

/// <summary>
/// Keeps the configured group's dashboard figures up to date: checks for changes every
/// <see cref="MiRefreshOptions.CheckInterval"/>, rebuilding when there are any, and rebuilds at once
/// when an admin asks. One refresh at a time in each instance; the store keeps instances apart.
/// </summary>
public sealed class MiRefreshService(
    IServiceScopeFactory scopeFactory,
    MiRefreshOptions options,
    CleanseRulesOptions rules,
    MiRefreshSignal signal,
    ILogger<MiRefreshService> logger) : BackgroundService
{
    /// <summary>How many mismatched issues a warning names, so a systematic fault cannot flood the log.</summary>
    private const int MismatchesLogged = 20;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await signal.WaitAsync(options.StartupDelay, stoppingToken);
            while (!stoppingToken.IsCancellationRequested)
            {
                await RefreshAsync(stoppingToken);
                await signal.WaitAsync(options.CheckInterval, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Shutting down.
        }
    }

    private async Task RefreshAsync(CancellationToken cancellationToken)
    {
        var force = signal.Begin();
        try
        {
            using var scope = scopeFactory.CreateScope();
            var dispatcher = scope.ServiceProvider.GetRequiredService<ICleanseDispatcher>();
            var result = await dispatcher.SendAsync(new RefreshMiCommand(rules.GroupId, force), cancellationToken);

            if (result.IsFailure)
            {
                logger.LogWarning("The dashboard figures for {GroupId} were not refreshed: {Error}", rules.GroupId, result.Error);
                return;
            }

            var status = result.Value.Status;
            if (result.Value.Rebuilt)
            {
                logger.LogInformation(
                    "Rebuilt the dashboard figures for {GroupId} in {DurationMilliseconds} ms from {Issues} issues, " +
                    "giving {Events} events.",
                    rules.GroupId, status.DurationMilliseconds, status.Issues, status.Events);

                if (status.Mismatches > 0)
                {
                    var ids = result.Value.MismatchedIssueIds ?? [];
                    var named = string.Join(", ", ids.Take(MismatchesLogged)) +
                                (ids.Count > MismatchesLogged ? $" and {ids.Count - MismatchesLogged} more" : string.Empty);
                    logger.LogWarning(
                        "{Mismatches} issues' histories in {GroupId} did not lead to their current state, so the " +
                        "dashboard counts a correction for each: {IssueIds}",
                        status.Mismatches, rules.GroupId, named);
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Never let one failure stop the loop; the next check tries again.
            logger.LogError(ex, "The dashboard figures for {GroupId} could not be refreshed, and will be tried again.", rules.GroupId);
        }
        finally
        {
            signal.End();
        }
    }
}
