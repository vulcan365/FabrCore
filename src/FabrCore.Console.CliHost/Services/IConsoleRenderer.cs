using FabrCore.Core;

namespace FabrCore.Console.CliHost.Services;

public interface IConsoleRenderer
{
    void ShowBanner();
    void ShowPrompt(string? currentAgent);
    void ShowAgentMessage(string text, string fromHandle);
    void ShowError(string text);
    void ShowWarning(string text);
    void ShowInfo(string text);
    void ShowSuccess(string text);
    void ShowAgentTable(IEnumerable<AgentInfo> agents);
    void ShowHealth(AgentHealthStatus health);
    void ShowHelp(IEnumerable<(string Name, string[] Aliases, string Description, string Usage)> commands);
    void ShowStatus(string userHandle, string? agentHandle, int port);
    Task ShowThinkingAsync(Func<Action<string>, CancellationToken, Task> work, CancellationToken ct);
    string? ShowAgentSelectionPrompt(IEnumerable<string> agentHandles);
    void ShowAgentCreationResults(List<AgentCreationResult> results);
}
