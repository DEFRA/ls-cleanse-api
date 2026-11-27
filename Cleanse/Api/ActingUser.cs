using Cleanse.Auth;
using Cleanse.Core;
using Cleanse.Core.Abstractions;
using Cleanse.Core.Domain;
using Microsoft.AspNetCore.Authorization;

namespace Cleanse.Api;

/// <summary>
/// Who a change is recorded against. By default that is the authenticated caller. The front end
/// acts for a signed-in person, and names them in <see cref="HeaderName"/> so the audit trail,
/// notes and actions show the person rather than the service. Only a caller trusted with the
/// user directory may do that, and only for a user the directory knows.
/// </summary>
public static class ActingUser
{
    public const string HeaderName = "x-cleanse-user-id";

    public static async Task<Result<Actor>> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        if (!context.Request.Headers.TryGetValue(HeaderName, out var values))
        {
            return context.User.ToActor();
        }

        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        if (!(await authorization.AuthorizeAsync(context.User, null, CleansePolicies.UserDirectory)).Succeeded)
        {
            return DomainErrors.ActingUserNotAllowed();
        }

        if (values.Count != 1 || !Guid.TryParse(values[0], out var id) || id == Guid.Empty)
        {
            return DomainErrors.Validation($"{HeaderName} must be one user id.");
        }

        var user = await context.RequestServices.GetRequiredService<IUserStore>().GetAsync(id, cancellationToken);
        return user is null
            ? DomainErrors.Validation($"{HeaderName} must identify a known user.")
            : Actor.Human(user.Id.ToString("D"), user.FullName);
    }
}
