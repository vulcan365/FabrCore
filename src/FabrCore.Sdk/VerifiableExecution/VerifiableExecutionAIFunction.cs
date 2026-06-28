using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using FabrCore.Core.VerifiableExecution;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk.VerifiableExecution;

internal sealed class VerifiableExecutionAIFunction : AIFunction
{
    private readonly AIFunction _inner;
    private readonly IVerifiableExecutionContext? _context;
    private readonly ExecutionRecordKind _kind;
    private readonly string _alias;
    private readonly string _method;
    private readonly ILogger? _logger;

    public VerifiableExecutionAIFunction(
        AIFunction inner,
        IVerifiableExecutionContext? context,
        ExecutionRecordKind kind,
        string alias,
        string method,
        ILogger? logger)
    {
        _inner = inner;
        _context = context;
        _kind = kind;
        _alias = alias;
        _method = method;
        _logger = logger;
    }

    public override string Name => _inner.Name;
    public override string Description => _inner.Description;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => _inner.AdditionalProperties;
    public override JsonElement JsonSchema => _inner.JsonSchema;
    public override JsonElement? ReturnJsonSchema => _inner.ReturnJsonSchema;
    public override MethodInfo? UnderlyingMethod => _inner.UnderlyingMethod;
    public override JsonSerializerOptions JsonSerializerOptions => _inner.JsonSerializerOptions;

    protected override async ValueTask<object?> InvokeCoreAsync(
        AIFunctionArguments arguments,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _inner.InvokeAsync(arguments, cancellationToken);
            sw.Stop();
            await _context.RecordToolInvocationAsync(
                _kind,
                _alias,
                _method,
                SnapshotArguments(arguments),
                result,
                sw.ElapsedMilliseconds,
                error: null,
                _logger,
                cancellationToken);

            return result;
        }
        catch (Exception ex)
        {
            sw.Stop();
            await _context.RecordToolInvocationAsync(
                _kind,
                _alias,
                _method,
                SnapshotArguments(arguments),
                result: null,
                sw.ElapsedMilliseconds,
                ex,
                _logger,
                cancellationToken);

            throw;
        }
    }

    private static IReadOnlyDictionary<string, object?> SnapshotArguments(AIFunctionArguments? arguments)
        => arguments is null
            ? new Dictionary<string, object?>(StringComparer.Ordinal)
            : arguments.ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);
}
