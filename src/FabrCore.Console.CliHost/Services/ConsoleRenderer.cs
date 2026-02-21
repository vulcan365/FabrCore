using FabrCore.Core;
using Spectre.Console;
using Color = Spectre.Console.Color;
using Panel = Spectre.Console.Panel;

namespace FabrCore.Console.CliHost.Services;

public class ConsoleRenderer : IConsoleRenderer
{
    public void ShowBanner()
    {
        AnsiConsole.Write(new Rule("[bold blue]FabrCore CLI[/]").RuleStyle("blue"));
        AnsiConsole.MarkupLine("[dim]Type a message to chat, or use /help for commands[/]");
        AnsiConsole.WriteLine();
    }

    public void ShowPrompt(string? currentAgent)
    {
        if (currentAgent != null)
            AnsiConsole.Markup($"[green]{Markup.Escape(currentAgent)}[/] > ");
        else
            AnsiConsole.Markup("[dim]no agent[/] > ");
    }

    public void ShowAgentMessage(string text, string fromHandle)
    {
        AnsiConsole.WriteLine();
        AnsiConsole.Write(new Panel(Markup.Escape(text))
            .Header($"[bold]{Markup.Escape(fromHandle)}[/]")
            .BorderColor(Color.Green)
            .Padding(1, 0));
        AnsiConsole.WriteLine();
    }

    public void ShowError(string text)
    {
        AnsiConsole.MarkupLine($"[red]Error:[/] {Markup.Escape(text)}");
    }

    public void ShowWarning(string text)
    {
        AnsiConsole.MarkupLine($"[yellow]Warning:[/] {Markup.Escape(text)}");
    }

    public void ShowInfo(string text)
    {
        AnsiConsole.MarkupLine($"[blue]{Markup.Escape(text)}[/]");
    }

    public void ShowSuccess(string text)
    {
        AnsiConsole.MarkupLine($"[green]{Markup.Escape(text)}[/]");
    }

    public void ShowAgentTable(IEnumerable<AgentInfo> agents)
    {
        var table = new Table()
            .Title("[bold]Available Agents[/]")
            .AddColumn("Handle")
            .AddColumn("Type")
            .AddColumn("Status")
            .AddColumn("Activated")
            .BorderColor(Color.Blue);

        foreach (var agent in agents)
        {
            var statusColor = agent.Status == AgentStatus.Active ? "green" : "dim";
            table.AddRow(
                Markup.Escape(agent.Handle),
                Markup.Escape(agent.AgentType),
                $"[{statusColor}]{agent.Status}[/]",
                agent.ActivatedAt.ToLocalTime().ToString("g")
            );
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public void ShowHealth(AgentHealthStatus health)
    {
        var grid = new Grid()
            .AddColumn()
            .AddColumn();

        grid.AddRow("[bold]Handle[/]", Markup.Escape(health.Handle));
        grid.AddRow("[bold]State[/]", FormatHealthState(health.State));
        grid.AddRow("[bold]Configured[/]", health.IsConfigured ? "[green]Yes[/]" : "[red]No[/]");

        if (health.AgentType != null)
            grid.AddRow("[bold]Agent Type[/]", Markup.Escape(health.AgentType));
        if (health.Uptime.HasValue)
            grid.AddRow("[bold]Uptime[/]", health.Uptime.Value.ToString(@"d\.hh\:mm\:ss"));
        if (health.MessagesProcessed.HasValue)
            grid.AddRow("[bold]Messages[/]", health.MessagesProcessed.Value.ToString());
        if (health.Message != null)
            grid.AddRow("[bold]Message[/]", Markup.Escape(health.Message));
        if (health.ActiveTimerCount.HasValue)
            grid.AddRow("[bold]Timers[/]", health.ActiveTimerCount.Value.ToString());
        if (health.ActiveReminderCount.HasValue)
            grid.AddRow("[bold]Reminders[/]", health.ActiveReminderCount.Value.ToString());
        if (health.StreamCount.HasValue)
            grid.AddRow("[bold]Streams[/]", health.StreamCount.Value.ToString());

        grid.AddRow("[bold]Timestamp[/]", health.Timestamp.ToLocalTime().ToString("g"));

        var panel = new Panel(grid)
            .Header("[bold]Agent Health[/]")
            .BorderColor(health.State == HealthState.Healthy ? Color.Green : Color.Yellow)
            .Padding(1, 0);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public void ShowHelp(IEnumerable<(string Name, string[] Aliases, string Description, string Usage)> commands)
    {
        var table = new Table()
            .Title("[bold]Commands[/]")
            .AddColumn("Command")
            .AddColumn("Aliases")
            .AddColumn("Description")
            .AddColumn("Usage")
            .BorderColor(Color.Blue);

        foreach (var (name, aliases, description, usage) in commands)
        {
            table.AddRow(
                $"[green]/{Markup.Escape(name)}[/]",
                aliases.Length > 0 ? string.Join(", ", aliases.Select(a => $"/{Markup.Escape(a)}")) : "[dim]-[/]",
                Markup.Escape(description),
                Markup.Escape(usage)
            );
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public void ShowAgentCreationResults(List<AgentCreationResult> results)
    {
        var table = new Table()
            .Title("[bold]Agent Creation Results[/]")
            .AddColumn("Handle")
            .AddColumn("Type")
            .AddColumn("Status")
            .BorderColor(Color.Blue);

        foreach (var result in results)
        {
            var status = result.Success
                ? "[green]Created[/]"
                : $"[red]Failed: {Markup.Escape(result.Error ?? "Unknown error")}[/]";

            table.AddRow(
                Markup.Escape(result.Handle),
                Markup.Escape(result.AgentType),
                status
            );
        }

        AnsiConsole.WriteLine();
        AnsiConsole.Write(table);
        AnsiConsole.WriteLine();
    }

    public void ShowStatus(string userHandle, string? agentHandle, int port)
    {
        var grid = new Grid()
            .AddColumn()
            .AddColumn();

        grid.AddRow("[bold]User Handle[/]", Markup.Escape(userHandle));
        grid.AddRow("[bold]Connected Agent[/]", agentHandle != null ? $"[green]{Markup.Escape(agentHandle)}[/]" : "[dim]none[/]");
        grid.AddRow("[bold]API Port[/]", port.ToString());
        grid.AddRow("[bold]Silo[/]", "[green]Running (in-process)[/]");

        var panel = new Panel(grid)
            .Header("[bold]Status[/]")
            .BorderColor(Color.Blue)
            .Padding(1, 0);

        AnsiConsole.WriteLine();
        AnsiConsole.Write(panel);
        AnsiConsole.WriteLine();
    }

    public async Task ShowThinkingAsync(Func<Action<string>, CancellationToken, Task> work, CancellationToken ct)
    {
        await AnsiConsole.Status()
            .Spinner(Spinner.Known.Dots)
            .SpinnerStyle(Style.Parse("blue"))
            .StartAsync("Thinking...", async ctx =>
            {
                void UpdateStatus(string text)
                {
                    ctx.Status(Markup.Escape(text));
                }

                await work(UpdateStatus, ct);
            });
    }

    public string? ShowAgentSelectionPrompt(IEnumerable<string> agentHandles)
    {
        var choices = agentHandles.ToList();
        if (choices.Count == 0)
            return null;

        const string cancel = "(Cancel)";
        choices.Add(cancel);

        var selected = AnsiConsole.Prompt(
            new SelectionPrompt<string>()
                .Title("Select an agent to connect to:")
                .AddChoices(choices));

        return selected == cancel ? null : selected;
    }

    private static string FormatHealthState(HealthState state)
    {
        return state switch
        {
            HealthState.Healthy => "[green]Healthy[/]",
            HealthState.Degraded => "[yellow]Degraded[/]",
            HealthState.Unhealthy => "[red]Unhealthy[/]",
            _ => $"[dim]{state}[/]"
        };
    }
}
