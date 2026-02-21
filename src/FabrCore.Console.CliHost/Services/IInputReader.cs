namespace FabrCore.Console.CliHost.Services;

public interface IInputReader
{
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken ct);
}
