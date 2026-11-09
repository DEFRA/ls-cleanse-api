using Cleanse.Core.Domain;

namespace Cleanse.Storage.Postgres.Schema;

internal sealed class UserRow
{
    public Guid Id { get; set; }
    public string? Issuer { get; set; }
    public string? Subject { get; set; }
    public string? TenantId { get; set; }
    public string? ObjectId { get; set; }
    public string? ClientId { get; set; }
    public required string FullName { get; set; }
    public DateTimeOffset? FirstLoggedInAtUtc { get; set; }
    public DateTimeOffset? LastLoggedInAtUtc { get; set; }
    public string? Email { get; set; }

    public KnownUser ToDomain() => new(
        Id, Issuer, Subject, TenantId, ObjectId, ClientId, FullName, FirstLoggedInAtUtc, LastLoggedInAtUtc, Email);
}
