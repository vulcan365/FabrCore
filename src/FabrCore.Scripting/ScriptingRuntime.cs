using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Microsoft.Extensions.Logging;

namespace FabrCore.Scripting;

/// <summary>Prepares reusable dependencies and supervises disposable worker processes. Not a security sandbox.</summary>
public sealed class ScriptingRuntime
{
    internal const string RoslynVersion = "5.0.0";
    private readonly ScriptingRuntimeOptions options;
    private readonly ILogger<ScriptingRuntime> logger;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 64 };

    public ScriptingRuntime(ScriptingRuntimeOptions options, ILogger<ScriptingRuntime> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DotnetPath);
        if (options.PreparationTimeout <= TimeSpan.Zero || options.PreparationTimeout > TimeSpan.FromHours(1)
            || options.MaxCodeCharacters <= 0 || options.MaxInputCharacters <= 0)
            throw new ArgumentException("Preparation timeout and input limits must be positive and timeout at most one hour.", nameof(options));
        // Snapshot mutable DI options: running environments cannot change underneath a plugin.
        this.options = new()
        {
            CacheDirectory = Path.GetFullPath(options.CacheDirectory),
            WorkingDirectory = Path.GetFullPath(options.WorkingDirectory),
            DotnetPath = options.DotnetPath,
            NuGetConfigPath = options.NuGetConfigPath is null ? null : Path.GetFullPath(options.NuGetConfigPath),
            PreparationTimeout = options.PreparationTimeout,
            AllowEnvironmentPreparation = options.AllowEnvironmentPreparation,
            MaxCodeCharacters = options.MaxCodeCharacters, MaxInputCharacters = options.MaxInputCharacters
        };
        this.logger = logger;
    }

    /// <summary>Prewarm a plugin using its normal configuration. Does not execute any generated script.</summary>
    public async Task<ScriptingEnvironmentInfo> PreparePluginAsync<TPlugin>(IServiceProvider services,
        CancellationToken cancellationToken = default) where TPlugin : CSharpScriptingPluginBase
    {
        ArgumentNullException.ThrowIfNull(services);
        await using var plugin = Microsoft.Extensions.DependencyInjection.ActivatorUtilities.CreateInstance<TPlugin>(services);
        await plugin.InitializeAsync(new(), services).WaitAsync(cancellationToken);
        return plugin.GetScriptingEnvironment();
    }

    internal async Task<PreparedEnvironment> PrepareAsync(EnvironmentDefinition definition, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.PreparationTimeout);
        var id = GetEnvironmentId(definition);
        var destination = Path.Combine(options.CacheDirectory, id);
        var prepared = new PreparedEnvironment(id, Path.Combine(destination, "publish"));
        if (IsReady(destination, id)) return prepared;
        if (!options.AllowEnvironmentPreparation)
            throw new InvalidOperationException($"Scripting environment '{id}' is not prepared. Prewarm the plugin with the .NET 10 SDK before disabling environment preparation.");

        Directory.CreateDirectory(options.CacheDirectory);
        await using var cacheLock = await AcquireLockAsync(Path.Combine(options.CacheDirectory, id + ".lock"), deadline.Token);
        if (IsReady(destination, id)) return prepared;
        var staging = Path.Combine(options.CacheDirectory, $".{id}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            logger.LogInformation("Preparing C# scripting environment {EnvironmentId} with {PackageCount} configured packages", id, definition.Packages.Count);
            await WriteWorkerProjectAsync(staging, definition, deadline.Token);
            var start = WorkerProcess.CreateStartInfo(options.DotnetPath, staging, execution: false);
            foreach (var argument in new[] { "publish", "Worker.csproj", "-c", "Release", "-o", "publish",
                "--self-contained", "false", "--runtime", RuntimeInformation.RuntimeIdentifier,
                "--nologo", "-v", "minimal", "-p:UseSharedCompilation=false", "--disable-build-servers" })
                start.ArgumentList.Add(argument);
            if (options.NuGetConfigPath != null) start.ArgumentList.Add("-p:RestoreConfigFile=" + options.NuGetConfigPath);
            var outcome = await WorkerProcess.RunAsync(start, options.PreparationTimeout, 32_768, null, false, deadline.Token);
            if (outcome.Failure == ScriptExecutionStatus.Cancelled) deadline.Token.ThrowIfCancellationRequested();
            if (outcome.ExitCode != 0 || outcome.Failure != null)
                throw new InvalidOperationException($"Could not prepare scripting environment '{id}'. A .NET 10 SDK and access to configured NuGet sources are required. {outcome.Failure}\n{outcome.Output}\n{outcome.Error}");
            await File.WriteAllTextAsync(Path.Combine(staging, "ready"), id, deadline.Token);
            if (Directory.Exists(destination)) DeleteOwnedDirectory(options.CacheDirectory, destination);
            await PublishDirectoryAsync(staging, destination, deadline.Token);
            return prepared;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Preparing scripting environment '{id}' exceeded {options.PreparationTimeout}.");
        }
        catch (System.ComponentModel.Win32Exception error)
        {
            throw new InvalidOperationException("Could not start dotnet to prepare the scripting worker. Install the .NET 10 SDK or configure ScriptingRuntimeOptions.DotnetPath.", error);
        }
        finally
        {
            if (Directory.Exists(staging)) DeleteOwnedDirectory(options.CacheDirectory, staging);
        }
    }

    internal async Task<ScriptExecutionResult> ExecuteAsync(PreparedEnvironment prepared, EnvironmentDefinition definition,
        string code, JsonElement? input, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > options.MaxCodeCharacters) throw new ArgumentException("Script exceeds the configured source size limit.", nameof(code));
        var value = input is { ValueKind: not JsonValueKind.Undefined } ? input.Value.Clone() : JsonSerializer.SerializeToElement<object?>(null);
        if (value.GetRawText().Length > options.MaxInputCharacters) throw new ArgumentException("Input exceeds the configured size limit.", nameof(input));
        if (cancellationToken.IsCancellationRequested) return Failure(ScriptExecutionStatus.Cancelled);
        var directory = Path.Combine(options.WorkingDirectory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var watch = Stopwatch.StartNew();
        ScriptExecutionResult result;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "request.json"),
                JsonSerializer.Serialize(new WorkerRequest(code, value, definition.Imports, definition.OutputLimit, definition.ArtifactLimit)), cancellationToken);
            var start = WorkerProcess.CreateStartInfo(options.DotnetPath, directory, execution: true);
            start.ArgumentList.Add(Path.Combine(prepared.Directory, "FabrCore.ScriptWorker.dll"));
            var outcome = await WorkerProcess.RunAsync(start, definition.Timeout, definition.OutputLimit,
                definition.MemoryLimitBytes, true, cancellationToken);
            if (outcome.Failure.HasValue) result = Failure(outcome.Failure.Value);
            else if (outcome.ExitCode != 0) result = new() { Status = ScriptExecutionStatus.WorkerError, Error = $"Worker exited with code {outcome.ExitCode}.", Output = (outcome.Output + outcome.Error)[..Math.Min(definition.OutputLimit, outcome.Output.Length + outcome.Error.Length)] };
            else result = await ReadResultAsync(directory, definition, cancellationToken);
        }
        catch (OperationCanceledException) { result = Failure(ScriptExecutionStatus.Cancelled); }
        catch (Exception error) when (error is IOException or JsonException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        { result = new() { Status = ScriptExecutionStatus.WorkerError, Error = "Worker could not complete: " + error.Message }; }
        finally
        {
            try { DeleteOwnedDirectory(options.WorkingDirectory, directory); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { logger.LogWarning(error, "Could not clean scripting execution directory {Directory}", directory); }
        }
        logger.LogInformation("C# execution in {EnvironmentId} completed with {Status} in {ElapsedMilliseconds} ms", prepared.Id, result.Status, watch.ElapsedMilliseconds);
        return result;
    }

    private static ScriptExecutionResult Failure(ScriptExecutionStatus status) => new() { Status = status, Error = status.ToString() };

    private static async Task<ScriptExecutionResult> ReadResultAsync(string directory, EnvironmentDefinition definition, CancellationToken cancellationToken)
    {
        var path = Path.Combine(directory, "result.json");
        var file = new FileInfo(path);
        if (!file.Exists || (file.Attributes & FileAttributes.ReparsePoint) != 0)
            return new() { Status = ScriptExecutionStatus.WorkerError, Error = "Worker did not produce a regular result file." };
        var maxBytes = 16L * definition.OutputLimit + 2L * definition.ArtifactLimit + 65_536;
        if (file.Length > maxBytes) return Failure(ScriptExecutionStatus.OutputLimitExceeded);
        // Bound reads as well as the file's observed size (a worker may have spawned another writer).
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var buffer = new MemoryStream();
        var block = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(block, cancellationToken)) != 0)
        {
            if (buffer.Length + read > maxBytes) return Failure(ScriptExecutionStatus.OutputLimitExceeded);
            buffer.Write(block, 0, read);
        }
        var result = JsonSerializer.Deserialize<ScriptExecutionResult>(buffer.ToArray(), JsonOptions)
            ?? throw new JsonException("Empty worker response.");
        if (!Enum.IsDefined(result.Status) || result.Output == null || result.Artifacts == null) throw new JsonException("Invalid worker response.");
        if (result.Output.Length > definition.OutputLimit || result.Error?.Length > definition.OutputLimit
            || result.Value?.GetRawText().Length > definition.OutputLimit
            || result.Artifacts.Count > 100 || result.Artifacts.Any(a => a is null || a.Content is null || string.IsNullOrWhiteSpace(a.Name)
                || a.Name.Contains('\\') || a.Name.StartsWith('/') || a.Name.Split('/').Any(p => p is ".." or "." || p.Contains(':')))
            || result.Artifacts.Sum(a => (long)a.Content.Length) > definition.ArtifactLimit)
            return Failure(ScriptExecutionStatus.OutputLimitExceeded);
        return result;
    }

    internal static string GetEnvironmentId(EnvironmentDefinition definition)
    {
        var identity = JsonSerializer.Serialize(new { Runtime = RuntimeInformation.RuntimeIdentifier, Framework = "net10.0",
            RoslynVersion, Packages = definition.Packages, Worker = ReadResource("Program.cs"), Protocol = ReadResource("Protocol.cs") });
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
    }

    private static bool IsReady(string directory, string id) => File.Exists(Path.Combine(directory, "ready"))
        && File.ReadAllText(Path.Combine(directory, "ready")) == id
        && File.Exists(Path.Combine(directory, "publish", "FabrCore.ScriptWorker.dll"))
        && File.Exists(Path.Combine(directory, "publish", "FabrCore.ScriptWorker.deps.json"))
        && File.Exists(Path.Combine(directory, "publish", "FabrCore.ScriptWorker.runtimeconfig.json"));

    private static async Task<FileStream> AcquireLockAsync(string path, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { await Task.Delay(100, cancellationToken); }
        }
    }

    private static async Task PublishDirectoryAsync(string source, string destination, CancellationToken cancellationToken)
    {
        // Windows can briefly retain a build directory handle after dotnet exits (including scanners).
        // Keep the environment lock held and retry the atomic rename; never mark a partial copy ready.
        for (var attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { Directory.Move(source, destination); return; }
            catch (IOException) when (OperatingSystem.IsWindows() && attempt < 30 && !Directory.Exists(destination))
            { await Task.Delay(100, cancellationToken); }
        }
    }

    private static async Task WriteWorkerProjectAsync(string directory, EnvironmentDefinition definition, CancellationToken token)
    {
        var project = new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup",
                new XElement("OutputType", "Exe"), new XElement("TargetFramework", "net10.0"),
                new XElement("AssemblyName", "FabrCore.ScriptWorker"), new XElement("ImplicitUsings", "enable"),
                new XElement("Nullable", "enable"), new XElement("UseAppHost", "false"),
                new XElement("RestorePackagesWithLockFile", "true"), new XElement("CopyLocalLockFileAssemblies", "true"),
                new XElement("PreserveCompilationReferences", "true")),
            new XElement("ItemGroup", Package("Microsoft.CodeAnalysis.CSharp.Scripting", RoslynVersion),
                definition.Packages.Select(p => Package(p.Key, p.Value))));
        await File.WriteAllTextAsync(Path.Combine(directory, "Worker.csproj"), project.ToString(), token);
        // Do not inherit a user's repository build customizations or central versions into the worker project.
        foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets", "Directory.Packages.props" })
            await File.WriteAllTextAsync(Path.Combine(directory, name), "<Project />", token);
        await File.WriteAllTextAsync(Path.Combine(directory, "global.json"), "{\"sdk\":{\"version\":\"10.0.100\",\"rollForward\":\"latestFeature\",\"allowPrerelease\":false}}", token);
        foreach (var name in new[] { "Program.cs", "Protocol.cs" })
            await File.WriteAllTextAsync(Path.Combine(directory, name), ReadResource(name), token);
        static XElement Package(string id, string version) => new("PackageReference", new XAttribute("Include", id), new XAttribute("Version", $"[{version}]"));
    }

    private static string ReadResource(string name)
    {
        using var stream = typeof(ScriptingRuntime).Assembly.GetManifestResourceStream("FabrCore.Scripting.Worker." + name)
            ?? throw new InvalidOperationException("Scripting worker resources are missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static void DeleteOwnedDirectory(string root, string directory)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(directory);
        if (!fullPath.StartsWith(fullRoot, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new IOException("Refusing cleanup outside the scripting directory.");
        if (!Directory.Exists(fullPath)) return;
        DeleteTree(new DirectoryInfo(fullPath));
        static void DeleteTree(DirectoryInfo folder)
        {
            if ((folder.Attributes & FileAttributes.ReparsePoint) == 0)
            {
                foreach (var entry in folder.EnumerateFileSystemInfos())
                {
                    if (entry is DirectoryInfo child) DeleteTree(child);
                    else entry.Delete();
                }
            }
            folder.Delete();
        }
    }
}

internal sealed record PreparedEnvironment(string Id, string Directory);
