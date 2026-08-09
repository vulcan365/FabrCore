using FabrCore.Host;

var builder = WebApplication.CreateBuilder(args);

// ── Simple path: FabrCore configures Orleans automatically from appsettings.json ──
// The application assembly is discovered automatically.
builder.AddFabrCoreServer(new FabrCoreServerOptions()
// Optional: custom providers (defaults work for most cases)
// .UseAgentManagementProvider<SqlAgentManagementProvider>()
// .UseAclEvaluator<MyAclEvaluator>()          // custom access-control decisions (see fabrcore-acl)
// .UseAuditProvider<MySiemAuditProvider>()    // durable security audit sink (see fabrcore-acl)
// .UseTimeProvider(new DemoTimeProvider()) // Orleans scheduling/timers/reminders only
);

// ── Advanced path: full Orleans control ──
// Use AddFabrCoreServices + UseOrleans + AddFabrCore instead of AddFabrCoreServer.
// See server-setup.md "Advanced Orleans Configuration" for details.
//
// using FabrCore.Host.Configuration;
//
// builder.AddFabrCoreServices();
//
// builder.Services.AddSingleton<TimeProvider>(new DemoTimeProvider());
//
// builder.UseOrleans(siloBuilder =>
// {
//     siloBuilder.UseLocalhostClustering();
//     siloBuilder.AddMemoryGrainStorage(FabrCoreOrleansConstants.StorageProviderName);
//     siloBuilder.AddMemoryGrainStorage(FabrCoreOrleansConstants.PubSubStoreName);
//     siloBuilder.AddMemoryStreams(FabrCoreOrleansConstants.StreamProviderName);
//     siloBuilder.UseInMemoryReminderService();
//     siloBuilder.AddFabrCore([]); // Entry assembly is automatic.
// });

var app = builder.Build();
app.UseFabrCoreServer();
app.Run();
