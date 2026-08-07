using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Hosting;

namespace FabrCore.Host.Configuration;

internal sealed class FabrCoreWebSocketOptionsValidator(
    IWebHostEnvironment environment,
    IOptions<FabrCoreHostOptions> hostOptions) : IValidateOptions<FabrCoreWebSocketOptions>
{
    public ValidateOptionsResult Validate(string? name, FabrCoreWebSocketOptions options)
    {
        if (!environment.IsDevelopment() && options.AllowDevelopmentPrincipalSelection)
            return ValidateOptionsResult.Fail("Development WebSocket principal selection cannot be enabled outside Development.");
        if (!environment.IsDevelopment() && hostOptions.Value.AllowedWebSocketOrigins.Count == 0)
            return ValidateOptionsResult.Fail("At least one FabrCore:Host:AllowedWebSocketOrigins entry is required outside Development.");
        if (options.MaxConcurrentRequests <= 0 || options.MaxDeliveriesPerPrincipal <= 0 || options.MaxClientsPerPrincipal <= 0)
            return ValidateOptionsResult.Fail("WebSocket concurrency, delivery, and client limits must be positive.");
        return ValidateOptionsResult.Success;
    }
}
