using Cleanse.Core.Abstractions;
using Cleanse.Core.Contracts;
using Cleanse.Core.Domain;

namespace Cleanse.Core.Commands.CodedValues;

/// <summary>Adds an entry to an admin-maintained list. New entries are active.</summary>
public sealed record AddCodedValueCommand(CodedList List, string Name, Actor Actor) : ICommand<CodedValueDto>;

internal sealed class AddCodedValueCommandHandler(
    ICodedValueStore store,
    IAuditStore audit,
    TimeProvider clock) : ICommandHandler<AddCodedValueCommand, CodedValueDto>
{
    public async Task<Result<CodedValueDto>> HandleAsync(
        AddCodedValueCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        var now = clock.GetUtcNow();
        var created = CodedValue.Create(command.List, command.Name, now);
        if (created.IsFailure)
        {
            return created.ToFailure<CodedValueDto>();
        }

        var value = created.Value;
        var existing = await store.ListAsync(command.List, cancellationToken);
        if (existing.Any(entry => entry.NameKey == value.NameKey) || !await store.AddAsync(value, cancellationToken))
        {
            return DomainErrors.CodedValueDuplicateName(command.List.ToWire(), value.Name);
        }

        await audit.AppendAsync(
            AuditEntry.ForCodedValue(
                value.Id,
                new IssueChange(
                    AuditEventKind.CodedValueAdded,
                    [
                        new AuditChange(nameof(CodedValue.List), null, value.List.ToWire()),
                        new AuditChange(nameof(CodedValue.Name), null, value.Name)
                    ]),
                command.Actor,
                now),
            cancellationToken);

        return value.ToDto();
    }
}

/// <summary>Switches an entry on or off. An entry switched off is no longer offered, but stays where it was used.</summary>
public sealed record SetCodedValueActiveCommand(
    CodedList List,
    Guid Id,
    bool IsActive,
    Actor Actor) : ICommand<CodedValueDto>;

internal sealed class SetCodedValueActiveCommandHandler(
    ICodedValueStore store,
    IAuditStore audit,
    TimeProvider clock) : ICommandHandler<SetCodedValueActiveCommand, CodedValueDto>
{
    public async Task<Result<CodedValueDto>> HandleAsync(
        SetCodedValueActiveCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        ArgumentNullException.ThrowIfNull(command.Actor);

        var value = await store.GetAsync(command.Id, cancellationToken);
        if (value is null || value.List != command.List)
        {
            return DomainErrors.CodedValueNotFound(command.Id);
        }

        var now = clock.GetUtcNow();
        var change = value.SetActive(command.IsActive, now);
        if (change.IsMaterial)
        {
            await store.SaveAsync(value, cancellationToken);
            await audit.AppendAsync(AuditEntry.ForCodedValue(value.Id, change, command.Actor, now), cancellationToken);
        }

        return value.ToDto();
    }
}
