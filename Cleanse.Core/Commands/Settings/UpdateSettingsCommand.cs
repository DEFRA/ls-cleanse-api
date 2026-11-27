using System.Globalization;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.Settings;

/// <summary>Sets how many days old an issue must be before the triage lists show it.</summary>
public sealed record UpdateSettingsCommand(int MinimumIssueAgeDays, Actor Actor) : ICommand<SettingsDto>;

internal sealed class UpdateSettingsCommandHandler(
    ISettingsStore store,
    IAuditStore audit,
    TimeProvider clock) : ICommandHandler<UpdateSettingsCommand, SettingsDto>
{
    public async Task<Result<SettingsDto>> HandleAsync(
        UpdateSettingsCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        var updated = ServiceSettings.Create(command.MinimumIssueAgeDays);
        if (updated.IsFailure)
        {
            return updated.ToFailure<SettingsDto>();
        }

        var current = await store.GetAsync(cancellationToken) ?? ServiceSettings.Default;
        if (current == updated.Value)
        {
            return current.ToDto();
        }

        var now = clock.GetUtcNow();
        await store.SaveAsync(updated.Value, cancellationToken);
        await audit.AppendAsync(
            AuditEntry.ForSetting(
                ServiceSettings.MinimumIssueAgeDaysName,
                new IssueChange(
                    AuditEventKind.SettingChanged,
                    [
                        new AuditChange(
                            nameof(ServiceSettings.MinimumIssueAgeDays),
                            current.MinimumIssueAgeDays.ToString(CultureInfo.InvariantCulture),
                            updated.Value.MinimumIssueAgeDays.ToString(CultureInfo.InvariantCulture))
                    ]),
                command.Actor,
                now),
            cancellationToken);

        return updated.Value.ToDto();
    }
}
