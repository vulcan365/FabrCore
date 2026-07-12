// Custom Microsoft 365 user -> FabrCore principal mapping.
// Register BEFORE AddMicrosoft365Copilot():
//     builder.Services.AddSingleton<ICopilotPrincipalResolver, {{RESOLVER_NAME}}>();
//
// The default resolver maps by Entra object id (see Microsoft365CopilotOptions.Principal).
// Implement your own when principals must come from your directory, a database lookup,
// or a composite key.

using FabrCore.Services.Microsoft365Copilot;
using Microsoft.Agents.Builder;

namespace {{NAMESPACE}};

public sealed class {{RESOLVER_NAME}} : ICopilotPrincipalResolver
{
    public async ValueTask<string?> ResolvePrincipalHandleAsync(
        ITurnContext turnContext,
        string? userAccessToken,
        CancellationToken cancellationToken)
    {
        // Entra identity stamped on the activity by Teams / Microsoft 365 Copilot.
        var objectId = turnContext.Activity.From?.AadObjectId;
        var tenantId = turnContext.Activity.From?.TenantId
            ?? turnContext.Activity.Conversation?.TenantId;

        if (objectId is null)
        {
            // Returning null rejects the message ("couldn't verify your identity").
            return null;
        }

        // Example: look the user up in your own store and return your canonical handle.
        // var user = await _directory.FindByEntraObjectIdAsync(objectId, cancellationToken);
        // return user?.FabrCoreHandle;

        // FabrCore handles must not contain ':' (principal/agent separator).
        // Keep handles stable — they partition agent state, storage, and ACL.
        await Task.CompletedTask;
        return $"m365-{objectId}".ToLowerInvariant();
    }
}
