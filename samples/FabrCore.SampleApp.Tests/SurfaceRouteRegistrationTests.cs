using FabrCore.Surface;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace FabrCore.SampleApp.Tests;

public class SurfaceRouteRegistrationTests
{
    [Fact]
    public void RepeatedSurfaceRouteRegistration_DoesNotThrow()
    {
        var routes = ResolveRoutePatterns(components => components
            .AddFabrCoreSurfaceRoutes()
            .AddFabrCoreSurfaceRoutes());

        Assert.Contains("/surface", routes);
    }

    [Fact]
    public void DuplicateRawAssemblyRegistration_StillThrows()
    {
        var exception = Record.Exception(() => ResolveRoutePatterns(components => components
            .AddAdditionalAssemblies(typeof(FabrCore.Surface.Components.SurfaceCommandCenter).Assembly)
            .AddAdditionalAssemblies(typeof(FabrCore.Surface.Components.SurfaceCommandCenter).Assembly)));

        Assert.IsType<InvalidOperationException>(exception);
        Assert.Contains("already defined", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static List<string> ResolveRoutePatterns(
        Action<RazorComponentsEndpointConventionBuilder> registerRoutes)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRazorComponents()
            .AddInteractiveServerComponents();

        var app = builder.Build();
        registerRoutes(app.MapRazorComponents<TestRootComponent>()
            .AddInteractiveServerRenderMode());

        return ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => "/" + (endpoint.RoutePattern.RawText ?? string.Empty).TrimStart('/'))
            .ToList();
    }

    private sealed class TestRootComponent : IComponent
    {
        public void Attach(RenderHandle renderHandle)
        {
        }

        public Task SetParametersAsync(ParameterView parameters) => Task.CompletedTask;
    }
}
