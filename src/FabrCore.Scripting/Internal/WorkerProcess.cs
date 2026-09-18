using System.Diagnostics;
using System.Text;

namespace FabrCore.Scripting;

internal sealed record ProcessOutcome(int ExitCode, string Output, string Error, ScriptExecutionStatus? Failure);

internal static class WorkerProcess
{
    internal static ProcessStartInfo CreateStartInfo(string executable, string workingDirectory, bool execution)
    {
        var info = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true
        };
        if (execution)
        {
            // Avoid forwarding host connection strings, API tokens and startup hooks.
            // This is environment hygiene, not a substitute for OS-level sandboxing.
            string[] allowed = ["PATH", "SystemRoot", "WINDIR", "COMSPEC", "DOTNET_ROOT", "DOTNET_ROOT_X64", "LANG", "LC_ALL", "TZ"];
            var values = allowed.ToDictionary(k => k, Environment.GetEnvironmentVariable);
            info.Environment.Clear();
            foreach (var (name, value) in values) if (value != null) info.Environment[name] = value;
            foreach (var name in new[] { "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE" }) info.Environment[name] = workingDirectory;
        }
        info.Environment["DOTNET_NOLOGO"] = "1";
        info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        if (!execution)
        {
            info.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            info.Environment["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
        }
        return info;
    }

    internal static async Task<ProcessOutcome> RunAsync(ProcessStartInfo info, TimeSpan timeout,
        int outputLimit, long? memoryLimit, bool killOnOutputLimit, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        using var process = new Process { StartInfo = info };
        var exceeded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        Task outputTask = Task.CompletedTask;
        Task errorTask = Task.CompletedTask;
        var started = false;
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            process.Start();
            started = true;
            process.StandardInput.Close();
            outputTask = DrainAsync(process.StandardOutput, stdout, outputLimit, exceeded, deadline.Token);
            errorTask = DrainAsync(process.StandardError, stderr, outputLimit, exceeded, deadline.Token);
            var exit = process.WaitForExitAsync(deadline.Token);
            ScriptExecutionStatus? failure = null;
            while (!exit.IsCompleted)
            {
                if (killOnOutputLimit && exceeded.Task.IsCompleted) { failure = ScriptExecutionStatus.OutputLimitExceeded; break; }
                if (memoryLimit.HasValue)
                {
                    process.Refresh();
                    if (!process.HasExited && process.WorkingSet64 > memoryLimit.Value)
                    { failure = ScriptExecutionStatus.MemoryLimitExceeded; break; }
                }
                await Task.WhenAny(exit, Task.Delay(50, deadline.Token));
                deadline.Token.ThrowIfCancellationRequested();
            }
            if (failure.HasValue) Kill(process);
            await exit;
            await Task.WhenAll(outputTask, errorTask).WaitAsync(deadline.Token);
            if (killOnOutputLimit && exceeded.Task.IsCompleted) failure ??= ScriptExecutionStatus.OutputLimitExceeded;
            return new(process.ExitCode, stdout.ToString(), stderr.ToString(), failure);
        }
        catch (OperationCanceledException)
        {
            if (started) Kill(process);
            return new(-1, "", "", cancellationToken.IsCancellationRequested
                ? ScriptExecutionStatus.Cancelled : ScriptExecutionStatus.TimedOut);
        }
        finally
        {
            if (started)
            {
                Kill(process);
                // Bound cleanup even when descendant processes retain inherited pipe handles.
                deadline.Cancel();
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
                try { await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (Exception) { }
            }
        }
    }

    private static async Task DrainAsync(StreamReader reader, StringBuilder text, int limit,
        TaskCompletionSource exceeded, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
        {
            var remaining = limit - text.Length;
            text.Append(buffer, 0, Math.Min(remaining, count));
            if (count > remaining) exceeded.TrySetResult();
        }
    }

    private static void Kill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }
}
