namespace FabrCore.Console.CliHost.Commands;

public interface ICliCommand
{
    string Name { get; }
    string Description { get; }
    string Usage { get; }
    string[] Aliases { get; }
    Task ExecuteAsync(string[] args, CancellationToken ct);
}
