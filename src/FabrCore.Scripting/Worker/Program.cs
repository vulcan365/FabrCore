using System.Reflection;
using System.Text;
using System.Text.Json;
using FabrCore.Scripting;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

var request = JsonSerializer.Deserialize<WorkerRequest>(await File.ReadAllTextAsync("request.json"))
    ?? throw new InvalidOperationException("Missing request.");
var output = new BoundedWriter(request.OutputLimit);
Console.SetOut(TextWriter.Synchronized(output));
Console.SetError(TextWriter.Synchronized(output));
Console.SetIn(new StringReader(""));
var outputDirectory = Path.GetFullPath("output");
Directory.CreateDirectory(outputDirectory);
ScriptExecutionResult result;
try
{
    // Runtime libraries supply implementation assemblies; refs/ supplies compile-only assets.
    var references = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    foreach (var file in ((string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        references[Path.GetFileName(file)] = file;
    foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory, "*.dll"))
        references[Path.GetFileName(file)] = file;
    var refsDirectory = Path.Combine(AppContext.BaseDirectory, "refs");
    if (Directory.Exists(refsDirectory))
        foreach (var file in Directory.EnumerateFiles(refsDirectory, "*.dll")) references.TryAdd(Path.GetFileName(file), file);
    var metadata = new List<MetadataReference>();
    foreach (var file in references.Values)
    {
        try { AssemblyName.GetAssemblyName(file); metadata.Add(MetadataReference.CreateFromFile(file)); }
        catch (BadImageFormatException) { /* Native package assets are not C# compiler references. */ }
    }
    var options = ScriptOptions.Default.WithReferences(metadata).WithImports(request.Imports)
        .WithMetadataResolver(null).WithSourceResolver(null);
    var value = await CSharpScript.EvaluateAsync<object?>(request.Code, options,
        new ScriptGlobals { Input = request.Input, OutputDirectory = outputDirectory });
    // Serialize through a bounded stream so a huge return value cannot allocate an unbounded JSON string.
    using var serialized = new BoundedStream(request.OutputLimit);
    using (var writer = new Utf8JsonWriter(serialized)) JsonSerializer.Serialize(writer, value, value?.GetType() ?? typeof(object));
    var element = JsonSerializer.Deserialize<JsonElement>(serialized.ToArray());
    var artifacts = new List<ScriptArtifact>();
    long total = 0;
    foreach (var file in Directory.EnumerateFiles(outputDirectory, "*", new EnumerationOptions
    { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint, IgnoreInaccessible = false }))
    {
        var size = new FileInfo(file).Length;
        total += size;
        if (total > request.ArtifactLimit || artifacts.Count >= 100) throw new OutputLimitException();
        await using var stream = File.OpenRead(file);
        using var bytes = new BoundedStream((int)Math.Min(request.ArtifactLimit - (total - size), int.MaxValue));
        await stream.CopyToAsync(bytes);
        artifacts.Add(new(Path.GetRelativePath(outputDirectory, file).Replace('\\', '/'), bytes.ToArray()));
    }
    if (output.Exceeded) throw new OutputLimitException();
    result = new() { Status = ScriptExecutionStatus.Success, Value = element, Output = output.ToString(), Artifacts = artifacts };
}
catch (CompilationErrorException error)
{
    result = new() { Status = ScriptExecutionStatus.CompilationError,
        Error = Limit(string.Join("\n", error.Diagnostics.Take(30)), request.OutputLimit), Output = output.ToString() };
}
catch (OutputLimitException)
{
    result = new() { Status = ScriptExecutionStatus.OutputLimitExceeded, Error = "Script output or artifacts exceeded the configured limit.", Output = output.ToString() };
}
catch (Exception error)
{
    result = new() { Status = ScriptExecutionStatus.RuntimeError,
        Error = Limit(error.GetType().Name + ": " + error.Message, request.OutputLimit), Output = output.ToString() };
}
await File.WriteAllTextAsync("result.json", JsonSerializer.Serialize(result));

static string Limit(string text, int limit) => text[..Math.Min(text.Length, limit)];

public sealed class ScriptGlobals
{
    public JsonElement Input { get; init; }
    public string InputJson => Input.GetRawText();
    public required string OutputDirectory { get; init; }
}

internal sealed class OutputLimitException : Exception;

internal sealed class BoundedWriter(int limit) : TextWriter
{
    private readonly StringBuilder text = new();
    public bool Exceeded { get; private set; }
    public override Encoding Encoding => Encoding.UTF8;
    public override void Write(char value)
    {
        if (text.Length >= limit) { Exceeded = true; throw new OutputLimitException(); }
        text.Append(value);
    }
    public override void Write(string? value)
    {
        if (value == null) return;
        var remaining = limit - text.Length;
        text.Append(value.AsSpan(0, Math.Min(value.Length, remaining)));
        if (value.Length > remaining) { Exceeded = true; throw new OutputLimitException(); }
    }
    public override string ToString() => text.ToString();
}

internal sealed class BoundedStream(int limit) : MemoryStream
{
    public override void Write(byte[] buffer, int offset, int count)
    { Check(count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer)
    { Check(buffer.Length); base.Write(buffer); }
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    { Check(buffer.Length); return base.WriteAsync(buffer, cancellationToken); }
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    { Check(count); return base.WriteAsync(buffer, offset, count, cancellationToken); }
    private void Check(int count)
    { if (Length + count > limit) throw new OutputLimitException(); }
}
