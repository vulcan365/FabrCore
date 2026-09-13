using FabrCore.Host.Security;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;

namespace FabrCore.Services.Connections;

internal sealed class ConnectionProtectionStartup(IFabrCoreDataProtectionProvider provider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        // Validate keys before accepting consent. A failed SQL/key provider never falls back to memory.
        var protector = provider.CreateProtector("FabrCore.Connections.Startup");
        var probe = Guid.NewGuid().ToString();
        if (protector.Unprotect(protector.Protect(probe)) != probe)
            throw new InvalidOperationException("Credential protection validation failed.");
        return Task.CompletedTask;
    }
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
