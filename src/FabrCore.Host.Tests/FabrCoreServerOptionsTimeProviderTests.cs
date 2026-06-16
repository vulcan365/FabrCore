using FabrCore.Host;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace FabrCore.Host.Tests;

[TestClass]
public sealed class FabrCoreServerOptionsTimeProviderTests
{
    [TestMethod]
    public void UseTimeProvider_WithInstance_RegistersSameInstanceAsTimeProviderAndConcreteType()
    {
        var provider = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-16T10:00:00Z"));
        var builder = WebApplication.CreateBuilder();

        builder.AddFabrCoreServices(new FabrCoreServerOptions().UseTimeProvider(provider));

        using var services = builder.Services.BuildServiceProvider();
        var resolvedTimeProvider = services.GetRequiredService<TimeProvider>();
        var resolvedConcreteProvider = services.GetRequiredService<ManualTimeProvider>();

        Assert.AreSame(provider, resolvedTimeProvider);
        Assert.AreSame(provider, resolvedConcreteProvider);
    }

    [TestMethod]
    public void UseTimeProvider_WithType_RegistersTypedSingletonAsTimeProviderAndConcreteType()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddFabrCoreServices(new FabrCoreServerOptions().UseTimeProvider<ManualTimeProvider>());

        using var services = builder.Services.BuildServiceProvider();
        var resolvedTimeProvider = services.GetRequiredService<TimeProvider>();
        var resolvedConcreteProvider = services.GetRequiredService<ManualTimeProvider>();

        Assert.AreSame(resolvedConcreteProvider, resolvedTimeProvider);
    }

    [TestMethod]
    public void AddFabrCoreServices_PreservesExistingTimeProvider_WhenOptionsDoNotSpecifyOne()
    {
        var provider = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-16T11:00:00Z"));
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<TimeProvider>(provider);

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var resolvedTimeProvider = services.GetRequiredService<TimeProvider>();

        Assert.AreSame(provider, resolvedTimeProvider);
    }

    [TestMethod]
    public void AddFabrCoreServices_UsesOptionTimeProvider_WhenExistingTimeProviderWasRegistered()
    {
        var existingProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-16T12:00:00Z"));
        var optionProvider = new ManualTimeProvider(DateTimeOffset.Parse("2026-06-16T13:00:00Z"));
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddSingleton<TimeProvider>(existingProvider);

        builder.AddFabrCoreServices(new FabrCoreServerOptions().UseTimeProvider(optionProvider));

        using var services = builder.Services.BuildServiceProvider();
        var resolvedTimeProvider = services.GetRequiredService<TimeProvider>();

        Assert.AreSame(optionProvider, resolvedTimeProvider);
    }

    [TestMethod]
    public void AddFabrCoreServices_RegistersSystemTimeProvider_WhenNoneWasConfigured()
    {
        var builder = WebApplication.CreateBuilder();

        builder.AddFabrCoreServices();

        using var services = builder.Services.BuildServiceProvider();
        var resolvedTimeProvider = services.GetRequiredService<TimeProvider>();

        Assert.AreSame(TimeProvider.System, resolvedTimeProvider);
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public ManualTimeProvider()
            : this(DateTimeOffset.Parse("2026-06-16T00:00:00Z"))
        {
        }

        public ManualTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
