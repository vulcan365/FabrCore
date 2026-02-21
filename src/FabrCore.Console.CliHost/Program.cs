using FabrCore.Client;
using FabrCore.Console.CliHost;
using FabrCore.Console.CliHost.Commands;
using FabrCore.Console.CliHost.Hosting;
using FabrCore.Console.CliHost.Services;
using FabrCore.Host;

var cliOptions = CliOptions.Parse(args);

var builder = WebApplication.CreateBuilder(args);

// Set up the full Orleans silo in-process
builder.AddFabrCoreServer();

// Client services (detects existing IClusterClient, skips duplicate Orleans config)
builder.AddFabrCoreClient();

// Register CLI options
builder.Services.AddSingleton(cliOptions);

// Register CLI services
builder.Services.AddSingleton<IConnectionManager, ConnectionManager>();
builder.Services.AddSingleton<IConsoleRenderer, ConsoleRenderer>();
builder.Services.AddSingleton<IInputReader, InputReader>();

// Register commands
builder.Services.AddSingleton<ICliCommand, HelpCommand>();
builder.Services.AddSingleton<ICliCommand, ClearCommand>();
builder.Services.AddSingleton<ICliCommand, ExitCommand>();
builder.Services.AddSingleton<ICliCommand, AgentsCommand>();
builder.Services.AddSingleton<ICliCommand, ConnectCommand>();
builder.Services.AddSingleton<ICliCommand, CreateCommand>();
builder.Services.AddSingleton<ICliCommand, HealthCommand>();
builder.Services.AddSingleton<ICliCommand, StatusCommand>();

// CommandRegistry depends on all ICliCommand instances
builder.Services.AddSingleton<CommandRegistry>();

// REPL hosted service
builder.Services.AddHostedService<CliHostedService>();

// Suppress all logs except our CLI namespace - keep the console clean for the REPL
builder.Logging.AddFilter("Orleans", LogLevel.None);
builder.Logging.AddFilter("Microsoft", LogLevel.None);
builder.Logging.AddFilter("FabrCore.Host", LogLevel.None);
builder.Logging.AddFilter("FabrCore.Client", LogLevel.None);
builder.Logging.AddFilter("FabrCore.Sdk", LogLevel.None);
builder.Logging.AddFilter("FabrCore.Core", LogLevel.None);
builder.Logging.AddFilter("FabrCore.Console.CliHost", LogLevel.None);
// Only our CLI services log at Info+ (to a file/debug, not console)
builder.Logging.SetMinimumLevel(LogLevel.None);

var app = builder.Build();

// Enable API controllers and WebSocket (needed for FabrCoreHostApiClient in-process)
app.UseFabrCoreServer();

await app.RunAsync();
