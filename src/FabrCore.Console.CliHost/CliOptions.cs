namespace FabrCore.Console.CliHost;

/// <summary>
/// CLI argument model parsed from command-line args.
/// </summary>
public class CliOptions
{
    /// <summary>
    /// User handle for identifying this CLI session.
    /// Default: cli-{MachineName}-{guid8}
    /// </summary>
    public string Handle { get; set; } = "cli-user";

    /// <summary>
    /// Kestrel port for the in-process API server.
    /// </summary>
    public int Port { get; set; } = 5846;

    public static CliOptions Parse(string[] args)
    {
        var options = new CliOptions();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--handle" when i + 1 < args.Length:
                    options.Handle = args[++i];
                    break;
                case "--port" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out var port))
                        options.Port = port;
                    break;
            }
        }

        return options;
    }
}
