using System.Text.Json;
using FabrCore.Console.CliHost.Commands;
using FabrCore.Console.CliHost.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FabrCore.Console.CliHost.Hosting;

public class CliHostedService : BackgroundService
{
    private readonly IConnectionManager _connection;
    private readonly IConsoleRenderer _renderer;
    private readonly IInputReader _inputReader;
    private readonly CommandRegistry _commandRegistry;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<CliHostedService> _logger;

    public CliHostedService(
        IConnectionManager connection,
        IConsoleRenderer renderer,
        IInputReader inputReader,
        CommandRegistry commandRegistry,
        IWebHostEnvironment env,
        ILogger<CliHostedService> logger)
    {
        _connection = connection;
        _renderer = renderer;
        _inputReader = inputReader;
        _commandRegistry = commandRegistry;
        _env = env;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Give the silo a moment to start up
        await Task.Delay(500, stoppingToken);

        _renderer.ShowBanner();

        try
        {
            _renderer.ShowInfo("Initializing connection...");
            await _connection.InitializeAsync(stoppingToken);
            _renderer.ShowSuccess($"Connected as: {_connection.CurrentHandle}");
            _renderer.ShowInfo("Type /help for available commands");
            System.Console.WriteLine();
        }
        catch (Exception ex)
        {
            _renderer.ShowError($"Failed to initialize: {ex.Message}");
            _logger.LogError(ex, "Failed to initialize connection");
            return;
        }

        await AutoLoadConfigAsync(stoppingToken);

        await RunReplAsync(stoppingToken);
    }

    private async Task AutoLoadConfigAsync(CancellationToken ct)
    {
        var fabrCorePath = Path.Combine(_env.ContentRootPath, "fabrcore.json");
        var cliHostPath = Path.Combine(_env.ContentRootPath, "fabrcore-clihost.json");

        if (!File.Exists(fabrCorePath) || !File.Exists(cliHostPath))
            return;

        try
        {
            var json = await File.ReadAllTextAsync(cliHostPath, ct);
            var config = JsonSerializer.Deserialize<CliHostConfiguration>(json);

            if (config?.Agents == null || config.Agents.Count == 0)
                return;

            _renderer.ShowInfo($"Found fabrcore-clihost.json — creating {config.Agents.Count} agent(s)...");
            var results = await _connection.CreateAgentsAsync(config.Agents, ct);
            _renderer.ShowAgentCreationResults(results);

            var firstSuccess = results.FirstOrDefault(r => r.Success);
            if (firstSuccess != null)
            {
                _renderer.ShowSuccess($"Connected to {firstSuccess.Handle}");
            }
        }
        catch (Exception ex)
        {
            _renderer.ShowWarning($"Auto-load of fabrcore-clihost.json failed: {ex.Message}");
            _logger.LogWarning(ex, "Failed to auto-load fabrcore-clihost.json");
        }

        System.Console.WriteLine();
    }

    private async Task RunReplAsync(CancellationToken ct)
    {
        await foreach (var line in _inputReader.ReadLinesAsync(ct))
        {
            if (ct.IsCancellationRequested)
                break;

            var trimmed = line.Trim();
            if (string.IsNullOrEmpty(trimmed))
            {
                _renderer.ShowPrompt(_connection.CurrentAgentHandle);
                continue;
            }

            if (trimmed.StartsWith('/'))
            {
                await HandleCommandAsync(trimmed, ct);
            }
            else
            {
                await HandleChatMessageAsync(trimmed, ct);
            }

            if (!ct.IsCancellationRequested)
                _renderer.ShowPrompt(_connection.CurrentAgentHandle);
        }
    }

    private async Task HandleCommandAsync(string input, CancellationToken ct)
    {
        // Parse: /commandName arg1 arg2 ...
        var parts = input[1..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;

        var commandName = parts[0];
        var args = parts.Length > 1 ? parts[1..] : [];

        var command = _commandRegistry.GetCommand(commandName);
        if (command == null)
        {
            _renderer.ShowError($"Unknown command: /{commandName}. Type /help for available commands.");
            return;
        }

        try
        {
            await command.ExecuteAsync(args, ct);
        }
        catch (Exception ex)
        {
            _renderer.ShowError($"Command failed: {ex.Message}");
            _logger.LogError(ex, "Command /{Command} failed", commandName);
        }
    }

    private async Task HandleChatMessageAsync(string message, CancellationToken ct)
    {
        if (!_connection.IsConnectedToAgent)
        {
            _renderer.ShowWarning("Not connected to any agent. Use /connect <handle> or /create <agentType> first.");
            return;
        }

        try
        {
            Core.AgentMessage? response = null;

            await _renderer.ShowThinkingAsync(async (updateStatus, thinkingCt) =>
            {
                // Wire thinking updates to spinner
                void OnThinking(string text) => updateStatus(text);
                _connection.ThinkingReceived += OnThinking;

                try
                {
                    response = await _connection.SendMessageAsync(message, thinkingCt);
                }
                finally
                {
                    _connection.ThinkingReceived -= OnThinking;
                }
            }, ct);

            if (response != null)
            {
                _renderer.ShowAgentMessage(
                    response.Message ?? "(empty response)",
                    response.FromHandle ?? "agent");
            }
        }
        catch (OperationCanceledException)
        {
            _renderer.ShowWarning("Request timed out.");
        }
        catch (Exception ex)
        {
            _renderer.ShowError($"Failed to send message: {ex.Message}");
            _logger.LogError(ex, "Failed to send chat message");
        }
    }
}
