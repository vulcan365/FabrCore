using System.Text.Json;
using FabrCore.Console.CliHost.Services;
using FabrCore.Core;
using Microsoft.AspNetCore.Hosting;

namespace FabrCore.Console.CliHost.Commands;

public class ConfigCommand : ICliCommand
{
    private readonly IConnectionManager _connection;
    private readonly IConsoleRenderer _renderer;
    private readonly FileDialogService _fileDialog;
    private readonly IWebHostEnvironment _env;

    public string Name => "config";
    public string Description => "Import a configuration file";
    public string Usage => "/config fabrcore|clihost";
    public string[] Aliases => ["cfg"];

    public ConfigCommand(
        IConnectionManager connection,
        IConsoleRenderer renderer,
        FileDialogService fileDialog,
        IWebHostEnvironment env)
    {
        _connection = connection;
        _renderer = renderer;
        _fileDialog = fileDialog;
        _env = env;
    }

    public async Task ExecuteAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0)
        {
            _renderer.ShowError("Usage: /config fabrcore|clihost");
            return;
        }

        switch (args[0].ToLowerInvariant())
        {
            case "fabrcore":
                await ImportFabrCoreConfig(ct);
                break;
            case "clihost":
                await ImportCliHostConfig(ct);
                break;
            default:
                _renderer.ShowError($"Unknown config type '{args[0]}'. Use 'fabrcore' or 'clihost'.");
                break;
        }
    }

    private async Task ImportFabrCoreConfig(CancellationToken ct)
    {
        _renderer.ShowInfo("Select a fabrcore.json file...");
        var filePath = _fileDialog.OpenFileDialog("Select fabrcore.json");

        if (filePath == null)
        {
            _renderer.ShowWarning("File selection cancelled.");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var config = JsonSerializer.Deserialize<FabrCoreConfiguration>(json);

            if (config == null)
            {
                _renderer.ShowError("Failed to parse configuration file.");
                return;
            }

            // Copy to ContentRootPath as fabrcore.json
            var destPath = Path.Combine(_env.ContentRootPath, "fabrcore.json");
            await File.WriteAllTextAsync(destPath, json, ct);

            _renderer.ShowSuccess(
                $"Imported {config.ModelConfigurations.Count} model configuration(s) and {config.ApiKeys.Count} API key(s).");
        }
        catch (JsonException ex)
        {
            _renderer.ShowError($"Invalid JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            _renderer.ShowError($"Failed to import config: {ex.Message}");
        }
    }

    private async Task ImportCliHostConfig(CancellationToken ct)
    {
        _renderer.ShowInfo("Select a fabrcore-clihost.json file...");
        var filePath = _fileDialog.OpenFileDialog("Select fabrcore-clihost.json");

        if (filePath == null)
        {
            _renderer.ShowWarning("File selection cancelled.");
            return;
        }

        try
        {
            var json = await File.ReadAllTextAsync(filePath, ct);
            var config = JsonSerializer.Deserialize<CliHostConfiguration>(json);

            if (config == null)
            {
                _renderer.ShowError("Failed to parse CLI host configuration file.");
                return;
            }

            if (config.Agents.Count == 0)
            {
                _renderer.ShowWarning("No agents defined in configuration file.");
                return;
            }

            _renderer.ShowInfo($"Creating {config.Agents.Count} agent(s)...");
            var results = await _connection.CreateAgentsAsync(config.Agents, ct);
            _renderer.ShowAgentCreationResults(results);

            var firstSuccess = results.FirstOrDefault(r => r.Success);
            if (firstSuccess != null)
            {
                _renderer.ShowSuccess($"Connected to {firstSuccess.Handle}");
            }
        }
        catch (JsonException ex)
        {
            _renderer.ShowError($"Invalid JSON: {ex.Message}");
        }
        catch (Exception ex)
        {
            _renderer.ShowError($"Failed to import CLI host config: {ex.Message}");
        }
    }
}
