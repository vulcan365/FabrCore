using System.Reflection;
using System.Text.Json;
using FabrCore.Sdk;
using Microsoft.Extensions.AI;

namespace FabrCore.Services.Memory.Plugin;

internal sealed class ScopedMemoryFunction(AIFunction inner, string writeScope, bool readOnly) : AIFunction, IScopedAgentMemoryTool
{
    internal bool ReadOnly { get; } = readOnly;
    public string WriteScope { get; } = writeScope;
    public override string Name => inner.Name;
    public override string Description => inner.Description;
    public override JsonElement JsonSchema => inner.JsonSchema;
    public override JsonElement? ReturnJsonSchema => inner.ReturnJsonSchema;
    public override MethodInfo? UnderlyingMethod => inner.UnderlyingMethod;
    public override JsonSerializerOptions JsonSerializerOptions => inner.JsonSerializerOptions;
    public override IReadOnlyDictionary<string, object?> AdditionalProperties => inner.AdditionalProperties;
    protected override ValueTask<object?> InvokeCoreAsync(AIFunctionArguments arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return inner.InvokeAsync(arguments, cancellationToken);
    }
}
