using FabrCore.Host;

var builder = WebApplication.CreateBuilder(args);

builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    // Add assemblies containing your agents, plugins, and tools
    AdditionalAssemblies = [
        // typeof(MyAgent).Assembly
    ]
});

var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
