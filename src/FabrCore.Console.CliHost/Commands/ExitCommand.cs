using FabrCore.Console.CliHost.Services;
using Microsoft.Extensions.Hosting;

namespace FabrCore.Console.CliHost.Commands;

public class ExitCommand : ICliCommand
{
    private readonly IHostApplicationLifetime _lifetime;
    private readonly IConsoleRenderer _renderer;

    public string Name => "exit";
    public string Description => "Exit the CLI";
    public string Usage => "/exit";
    public string[] Aliases => ["quit", "q"];

    public ExitCommand(IHostApplicationLifetime lifetime, IConsoleRenderer renderer)
    {
        _lifetime = lifetime;
        _renderer = renderer;
    }

    public Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        _renderer.ShowInfo("Shutting down...");
        _lifetime.StopApplication();

        // Console.ReadLine() blocks and cannot be cancelled, so force exit
        // after a brief delay to let the host begin graceful shutdown.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1000);
            Environment.Exit(0);
        });

        return Task.CompletedTask;
    }
}
