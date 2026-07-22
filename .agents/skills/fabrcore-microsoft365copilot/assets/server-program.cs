// FabrCore server Program.cs with the Microsoft 365 Copilot channel enabled.
// Requires: <PackageReference Include="FabrCore.Host" /> and
//           <PackageReference Include="FabrCore.Services.Microsoft365Copilot" />
// Configuration comes from the "Microsoft365Copilot" section of fabrcore.json
// (see assets/fabrcore-json-microsoft365copilot.json).

using FabrCore.Host;
using FabrCore.Services.Microsoft365Copilot;

var builder = WebApplication.CreateBuilder(args);

builder.AddFabrCoreServer(new FabrCoreServerOptions
{
    // Assemblies containing your [AgentAlias]/[PluginAlias]/[ToolAlias] types.
    AdditionalAssemblies = [typeof({{AGENT_TYPE}}).Assembly]
});

// Microsoft 365 Copilot / Teams channel. Optional lambda overrides config values in code:
// builder.AddMicrosoft365Copilot(o => o.Streaming.InformativeUpdate = "Thinking...");
builder.AddMicrosoft365Copilot();

// Production + scale-out/SSO: register a durable Agents SDK IStorage BEFORE
// AddMicrosoft365Copilot (sign-in and turn state default to in-memory):
// builder.Services.AddSingleton<Microsoft.Agents.Storage.IStorage>(
//     new Microsoft.Agents.Storage.Blobs.BlobsStorage("<connection>", "m365-state"));

var app = builder.Build();

app.UseFabrCoreServer();
app.UseMicrosoft365Copilot();   // maps POST /api/messages (+ dev app-package endpoints)

app.Run();
