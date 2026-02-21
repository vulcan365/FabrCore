using FabrCore.Console.CliHost.Services;

namespace FabrCore.Console.CliHost.Commands;

public class ClearCommand : ICliCommand
{
    private readonly IConsoleRenderer _renderer;

    public string Name => "clear";
    public string Description => "Clear the screen";
    public string Usage => "/clear";
    public string[] Aliases => ["cls"];

    public ClearCommand(IConsoleRenderer renderer)
    {
        _renderer = renderer;
    }

    public Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        System.Console.Clear();
        _renderer.ShowBanner();
        return Task.CompletedTask;
    }
}
