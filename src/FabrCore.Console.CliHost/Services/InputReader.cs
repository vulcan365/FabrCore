using System.Runtime.CompilerServices;

namespace FabrCore.Console.CliHost.Services;

public class InputReader : IInputReader
{
    public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await Task.Run(() =>
            {
                try
                {
                    return System.Console.ReadLine();
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }, ct);

            if (line == null)
                yield break;

            yield return line;
        }
    }
}
