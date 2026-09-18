using System.Text.Json;
using FabrCore.Core;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace FabrCore.Scripting.Tests;

[TestClass]
public sealed class EnvironmentConfigurationTests
{
    [TestMethod]
    public void VersionsAreExactAndConflictsAreRejected()
    {
        var builder = new ScriptingEnvironmentBuilder().AddPackage("Newtonsoft.Json", "13.0.3");
        builder.AddPackage("NEWTONSOFT.JSON", "13.0.3");
        Assert.HasCount(1, builder.Build().Packages);
        Assert.Throws<ArgumentException>(() => builder.AddPackage("Newtonsoft.Json", "13.0.1"));
        foreach (var version in new[] { "*", "13.*", "[13.0.1,14.0.0)", "latest", "$(Version)" })
            Assert.Throws<ArgumentException>(() => new ScriptingEnvironmentBuilder().AddPackage("Example", version));
        Assert.Throws<ArgumentException>(() => builder.AddPackage("../package", "1.0.0"));
        Assert.Throws<ArgumentException>(() => builder.AddPackage("Microsoft.CodeAnalysis.CSharp", "4.0.0"));
    }

    [TestMethod]
    public void DependenciesDetermineCacheIdentityButExecutionPolicyDoesNot()
    {
        var first = new ScriptingEnvironmentBuilder().AddPackage("B", "1.0.0").AddPackage("A", "2.0.0").Build();
        var second = new ScriptingEnvironmentBuilder().AddPackage("a", "2.0.0").AddPackage("b", "1.0.0")
            .WithInstructions("Different agent").WithTimeout(TimeSpan.FromSeconds(1)).Build();
        Assert.AreEqual(ScriptingRuntime.GetEnvironmentId(first), ScriptingRuntime.GetEnvironmentId(second));
        Assert.AreNotEqual(ScriptingRuntime.GetEnvironmentId(first), ScriptingRuntime.GetEnvironmentId(
            new ScriptingEnvironmentBuilder().AddPackage("a", "2.0.1").AddPackage("b", "1.0.0").Build()));
    }

    [TestMethod]
    public void BuildSnapshotsConfigurationAndRejectsInvalidLimits()
    {
        var builder = new ScriptingEnvironmentBuilder();
        var snapshot = builder.Build();
        builder.AddPackage("Example", "1.0.0");
        Assert.IsEmpty(snapshot.Packages);
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithTimeout(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithMemoryLimit(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => builder.WithArtifactLimit(-1));
        Assert.Throws<ArgumentException>(() => builder.AddImports("System; return 1;"));
    }

    [TestMethod]
    public async Task OfflinePreparationFailsClearlyWithoutLaunchingDotnet()
    {
        var runtime = new ScriptingRuntime(new()
        {
            CacheDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")),
            AllowEnvironmentPreparation = false, DotnetPath = "this-must-not-run"
        }, NullLogger<ScriptingRuntime>.Instance);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.PrepareAsync(new ScriptingEnvironmentBuilder().Build(), default));
        StringAssert.Contains(error.Message, "Prewarm");
    }

    [TestMethod]
    public void WorkerDoesNotInheritHostSecretsOrStartupHooks()
    {
        var info = WorkerProcess.CreateStartInfo("dotnet", Path.GetTempPath(), execution: true);
        Assert.IsFalse(info.UseShellExecute);
        Assert.IsTrue(info.CreateNoWindow);
        CollectionAssert.AreEquivalent(new[] { "PATH", "SystemRoot", "WINDIR", "COMSPEC", "DOTNET_ROOT", "DOTNET_ROOT_X64", "LANG", "LC_ALL", "TZ",
            "TEMP", "TMP", "TMPDIR", "HOME", "USERPROFILE", "DOTNET_NOLOGO", "DOTNET_CLI_TELEMETRY_OPTOUT" }
            .Where(name => info.Environment.ContainsKey(name)).ToArray(), info.Environment.Keys.ToArray());
        Assert.IsFalse(info.Environment.ContainsKey("DOTNET_STARTUP_HOOKS"));
    }
}

[TestClass]
[TestCategory("Integration")]
public sealed class ScriptingWorkerTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "fabrcore-scripting-tests", Guid.NewGuid().ToString("N"));
    private static ServiceProvider services = null!;

    [ClassInitialize]
    public static void Initialize(TestContext context)
    {
        services = new ServiceCollection().AddLogging().AddFabrCoreScripting(options =>
        {
            options.CacheDirectory = Path.Combine(Root, "cache");
            options.WorkingDirectory = Path.Combine(Root, "runs");
        }).BuildServiceProvider();
    }

    [ClassCleanup]
    public static async Task Cleanup()
    {
        await services.DisposeAsync();
        ScriptingRuntime.DeleteOwnedDirectory(Path.GetDirectoryName(Root)!, Root);
    }

    [TestMethod]
    public async Task InheritedToolsAreDiscoveredAndNewtonsoftRunsInAnotherProcess()
    {
        var registry = new FabrCoreToolRegistry(NullLogger<FabrCoreToolRegistry>.Instance, [typeof(JsonScripts).Assembly]);
        await using var scope = await registry.ResolveToolScopeAsync(services, ["test-json-scripts"], [], new AgentConfiguration());
        CollectionAssert.AreEquivalent(new[] { "ExecuteCSharp", "GetScriptingEnvironment" }, scope.Tools.Select(t => t.Name).ToArray());
        var function = (AIFunction)scope.Tools.Single(t => t.Name == "ExecuteCSharp");
        var value = await function.InvokeAsync(new AIFunctionArguments
        {
            ["code"] = "return new { Pid = System.Environment.ProcessId, Name = Newtonsoft.Json.Linq.JObject.Parse(InputJson)[\"name\"]!.ToString() };",
            ["input"] = JsonSerializer.SerializeToElement(new { name = "Eric" })
        });
        var result = JsonSerializer.SerializeToElement(value).Deserialize<ScriptExecutionResult>(new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        Assert.AreEqual(ScriptExecutionStatus.Success, result.Status, result.Error);
        Assert.AreEqual("Eric", result.Value!.Value.GetProperty("Name").GetString());
        Assert.AreNotEqual(Environment.ProcessId, result.Value.Value.GetProperty("Pid").GetInt32());
    }

    [TestMethod]
    public async Task SharedEnvironmentHasIndependentFilesAndVariablesAndReturnsArtifacts()
    {
        await using var first = await Create<JsonScripts>();
        await using var second = await Create<JsonScripts>();
        Assert.AreEqual(first.GetScriptingEnvironment().EnvironmentId, second.GetScriptingEnvironment().EnvironmentId);
        var result = await first.ExecuteCSharp("""
            System.IO.File.WriteAllText("private.txt", "first agent");
            System.IO.File.WriteAllText(System.IO.Path.Combine(OutputDirectory, "report.txt"), "report");
            int saved = 42;
            Console.WriteLine("hello");
            return saved;
            """);
        Assert.AreEqual(ScriptExecutionStatus.Success, result.Status, result.Error);
        StringAssert.Contains(result.Output, "hello");
        Assert.AreEqual("report", System.Text.Encoding.UTF8.GetString(result.Artifacts.Single().Content));
        var next = await second.ExecuteCSharp("return System.IO.File.Exists(\"private.txt\");");
        Assert.IsFalse(next.Value!.Value.GetBoolean());
        Assert.AreEqual(ScriptExecutionStatus.CompilationError, (await first.ExecuteCSharp("return saved;")).Status);
        Assert.IsEmpty(Directory.GetDirectories(Path.Combine(Root, "runs")));
    }

    [TestMethod]
    public async Task DifferentPackageSetsAndVersionsRemainSeparate()
    {
        await using var json = await Create<JsonScripts>();
        await using var other = await Create<OtherJsonScripts>();
        await using var bare = await Create<BareScripts>();
        Assert.AreNotEqual(json.GetScriptingEnvironment().EnvironmentId, other.GetScriptingEnvironment().EnvironmentId);
        var code = "return System.Diagnostics.FileVersionInfo.GetVersionInfo(typeof(Newtonsoft.Json.JsonConvert).Assembly.Location).FileVersion;";
        var first = await json.ExecuteCSharp(code);
        var second = await other.ExecuteCSharp(code);
        Assert.AreEqual(ScriptExecutionStatus.Success, first.Status, first.Error);
        Assert.AreEqual(ScriptExecutionStatus.Success, second.Status, second.Error);
        Assert.AreNotEqual(first.Value!.Value.GetString(), second.Value!.Value.GetString());
        Assert.AreEqual(ScriptExecutionStatus.CompilationError, (await bare.ExecuteCSharp(code)).Status);
    }

    [TestMethod]
    public async Task CompilationRuntimeAndDirectiveFailuresReturnUsefulErrors()
    {
        await using var plugin = await Create<JsonScripts>();
        var compile = await plugin.ExecuteCSharp("return DoesNotExist;");
        Assert.AreEqual(ScriptExecutionStatus.CompilationError, compile.Status);
        StringAssert.Contains(compile.Error!, "DoesNotExist");
        var runtime = await plugin.ExecuteCSharp("throw new InvalidOperationException(\"expected failure\");");
        Assert.AreEqual(ScriptExecutionStatus.RuntimeError, runtime.Status);
        StringAssert.Contains(runtime.Error!, "expected failure");
        Assert.AreEqual(ScriptExecutionStatus.CompilationError, (await plugin.ExecuteCSharp("#r \"nuget: Example, 1.0.0\"\nreturn 1;")).Status);
        Assert.AreEqual(ScriptExecutionStatus.CompilationError, (await plugin.ExecuteCSharp("#load \"external.csx\"\nreturn 1;")).Status);
    }

    [TestMethod]
    public async Task HungScriptsAreKilledAndNextExecutionWorks()
    {
        await using var plugin = await Create<ShortScripts>();
        Assert.AreEqual(ScriptExecutionStatus.TimedOut, (await plugin.ExecuteCSharp("while (true) { }")).Status);
        Assert.AreEqual(ScriptExecutionStatus.Success, (await plugin.ExecuteCSharp("return 7;")).Status);
    }

    [TestMethod]
    public async Task CallerCancellationAndPluginDisposalStopRunningWorkers()
    {
        await using var plugin = await Create<JsonScripts>();
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(ScriptExecutionStatus.Cancelled, (await plugin.ExecuteCSharp("while (true) { }", cancellationToken: cancel.Token)).Status);
        var pending = plugin.ExecuteCSharp("while (true) { }");
        await plugin.DisposeAsync();
        Assert.AreEqual(ScriptExecutionStatus.Cancelled, (await pending).Status);
        await plugin.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => plugin.ExecuteCSharp("return 1;"));
    }

    [TestMethod]
    public async Task OutputArtifactsAndAbruptExitAreBounded()
    {
        await using var plugin = await Create<LimitedScripts>();
        Assert.AreEqual(ScriptExecutionStatus.OutputLimitExceeded,
            (await plugin.ExecuteCSharp("Console.Write(new string('x', 4096)); return 1;")).Status);
        Assert.AreEqual(ScriptExecutionStatus.OutputLimitExceeded,
            (await plugin.ExecuteCSharp("return new string('x', 4096);")).Status);
        Assert.AreEqual(ScriptExecutionStatus.OutputLimitExceeded,
            (await plugin.ExecuteCSharp("System.IO.File.WriteAllBytes(System.IO.Path.Combine(OutputDirectory, \"large\"), new byte[4096]); return 1;")).Status);
        Assert.AreEqual(ScriptExecutionStatus.WorkerError,
            (await plugin.ExecuteCSharp("System.Environment.Exit(12); return 1;")).Status);
    }

    [TestMethod]
    public async Task WarmCacheWorksWithPreparationDisabledAndMissingSdk()
    {
        await using var original = await Create<JsonScripts>();
        using var offline = new ServiceCollection().AddLogging().AddFabrCoreScripting(options =>
        {
            options.CacheDirectory = Path.Combine(Root, "cache");
            options.AllowEnvironmentPreparation = false;
            options.DotnetPath = "missing-sdk-host";
        }).BuildServiceProvider();
        await using var plugin = new JsonScripts();
        await plugin.InitializeAsync(new(), offline);
        Assert.AreEqual(original.GetScriptingEnvironment().EnvironmentId, plugin.GetScriptingEnvironment().EnvironmentId);
    }

    [TestMethod]
    public async Task TransitivePackageDependenciesAreAvailableAtCompileAndRuntime()
    {
        await using var plugin = await Create<ConfigurationScripts>();
        var result = await plugin.ExecuteCSharp("""
            using Microsoft.Extensions.Configuration;
            using (var stream = new System.IO.MemoryStream(System.Text.Encoding.UTF8.GetBytes("{\"answer\":\"42\"}")))
            {
                var config = new ConfigurationBuilder().AddJsonStream(stream).Build();
                return config["answer"];
            }
            """);
        Assert.AreEqual(ScriptExecutionStatus.Success, result.Status, result.Error);
        Assert.AreEqual("42", result.Value!.Value.GetString());
    }

    [TestMethod]
    public async Task FailedRestoreDoesNotPublishAnEnvironment()
    {
        var folder = Path.Combine(Root, "failed-restore");
        Directory.CreateDirectory(Path.Combine(folder, "feed"));
        var nuget = Path.Combine(folder, "NuGet.Config");
        await File.WriteAllTextAsync(nuget, "<configuration><packageSources><clear/><add key=\"empty\" value=\"feed\"/></packageSources></configuration>");
        var runtime = new ScriptingRuntime(new()
        {
            CacheDirectory = Path.Combine(folder, "cache"), NuGetConfigPath = nuget
        }, NullLogger<ScriptingRuntime>.Instance);
        var definition = new ScriptingEnvironmentBuilder().AddPackage("FabrCore.DoesNotExist.Scripting.Integration", "0.0.1").Build();
        await Assert.ThrowsAsync<InvalidOperationException>(() => runtime.PrepareAsync(definition, default));
        Assert.IsFalse(Directory.Exists(Path.Combine(folder, "cache", ScriptingRuntime.GetEnvironmentId(definition))));
        Assert.IsEmpty(Directory.GetDirectories(Path.Combine(folder, "cache")));
    }

    [TestMethod]
    public async Task ConcurrentPreparationPublishesOneCompleteEnvironment()
    {
        var cache = Path.Combine(Root, "concurrent-cache");
        var firstRuntime = new ScriptingRuntime(new() { CacheDirectory = cache }, NullLogger<ScriptingRuntime>.Instance);
        var secondRuntime = new ScriptingRuntime(new() { CacheDirectory = cache }, NullLogger<ScriptingRuntime>.Instance);
        var definition = new ScriptingEnvironmentBuilder().Build();
        var prepared = await Task.WhenAll(firstRuntime.PrepareAsync(definition, default), secondRuntime.PrepareAsync(definition, default));
        Assert.AreEqual(prepared[0], prepared[1]);
        Assert.HasCount(1, Directory.GetDirectories(cache));
        Assert.IsTrue(File.Exists(Path.Combine(prepared[0].Directory, "FabrCore.ScriptWorker.dll")));
    }

    [TestMethod]
    public async Task WorkingSetLimitTerminatesAnOversizedWorker()
    {
        await using var plugin = await Create<MemoryScripts>();
        var result = await plugin.ExecuteCSharp("""
            var bytes = new byte[256 * 1024 * 1024];
            for (int i = 0; i < bytes.Length; i += 4096) bytes[i] = 1;
            await Task.Delay(10000);
            return bytes.Length;
            """);
        Assert.AreEqual(ScriptExecutionStatus.MemoryLimitExceeded, result.Status, result.Error);
    }

    private static async Task<T> Create<T>() where T : CSharpScriptingPluginBase, new()
    {
        var plugin = new T();
        try { await plugin.InitializeAsync(new(), services); return plugin; }
        catch { await plugin.DisposeAsync(); throw; }
    }

    [PluginAlias("test-json-scripts")]
    public sealed class JsonScripts : CSharpScriptingPluginBase
    {
        protected override void Configure(ScriptingEnvironmentBuilder environment) => environment
            .AddPackage("Newtonsoft.Json", "13.0.3").WithInstructions("Use Newtonsoft for JSON.");
    }
    public sealed class OtherJsonScripts : CSharpScriptingPluginBase
    { protected override void Configure(ScriptingEnvironmentBuilder environment) => environment.AddPackage("Newtonsoft.Json", "13.0.1"); }
    public sealed class BareScripts : CSharpScriptingPluginBase
    { protected override void Configure(ScriptingEnvironmentBuilder environment) { } }
    public sealed class ShortScripts : CSharpScriptingPluginBase
    { protected override void Configure(ScriptingEnvironmentBuilder environment) => environment.WithTimeout(TimeSpan.FromSeconds(5)); }
    public sealed class LimitedScripts : CSharpScriptingPluginBase
    { protected override void Configure(ScriptingEnvironmentBuilder environment) => environment.WithOutputLimit(1024).WithArtifactLimit(1024); }
    public sealed class ConfigurationScripts : CSharpScriptingPluginBase
    { protected override void Configure(ScriptingEnvironmentBuilder environment) => environment.AddPackage("Microsoft.Extensions.Configuration.Json", "10.0.12"); }
    public sealed class MemoryScripts : CSharpScriptingPluginBase
    { protected override void Configure(ScriptingEnvironmentBuilder environment) => environment.WithMemoryLimit(128L * 1024 * 1024); }
}
