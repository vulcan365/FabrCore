using FabrCore.Host;

var builder = WebApplication.CreateBuilder(args);

// ── Simple path: FabrCore configures Orleans automatically from appsettings.json ──
builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    // Add assemblies containing your agents, plugins, and tools
    AdditionalAssemblies = [
        // typeof(MyAgent).Assembly
    ]
}
// Optional: custom providers (defaults work for most cases)
// .UseAgentManagementProvider<SqlAgentManagementProvider>()
// .UseAclProvider<SqlAclProvider>()
);

// ── Advanced path: full Orleans control ──
// Use AddFabrCoreServices + UseOrleans + AddFabrCore instead of AddFabrCoreServer.
// See server-setup.md "Advanced Orleans Configuration" for details.
//
// using FabrCore.Host.Configuration;
//
// builder.AddFabrCoreServices(new FabrCoreServerOptions
// {
//     AdditionalAssemblies = [typeof(MyAgent).Assembly]
// });
//
// builder.UseOrleans(siloBuilder =>
// {
//     siloBuilder.UseLocalhostClustering();
//     siloBuilder.AddMemoryGrainStorage(FabrCoreOrleansConstants.StorageProviderName);
//     siloBuilder.AddMemoryGrainStorage(FabrCoreOrleansConstants.PubSubStoreName);
//     siloBuilder.AddMemoryStreams(FabrCoreOrleansConstants.StreamProviderName);
//     siloBuilder.UseInMemoryReminderService();
//     siloBuilder.AddFabrCore([typeof(MyAgent).Assembly]);
// });

var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
