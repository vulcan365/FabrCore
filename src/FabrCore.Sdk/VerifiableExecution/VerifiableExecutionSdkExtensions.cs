using System.Diagnostics;
using FabrCore.Core.VerifiableExecution;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk.VerifiableExecution;

public static class VerifiableExecutionSdkExtensions
{
    public static Task<VerifiableExecutionEffectResult<T>> RecordDbEffectAsync<T>(
        this IVerifiableExecutionContext? context,
        string operation,
        string target,
        string? subject,
        Func<Task<T>> effect,
        IReadOnlyDictionary<string, string?>? metadata = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
        => context.RecordExternalEffectAsync(
            ExecutionRecordKind.ExternalDbEffect,
            new VerifiableExecutionEffectOptions
            {
                Operation = operation,
                Target = target,
                Subject = subject,
                ComponentType = "db",
                Metadata = metadata
            },
            effect,
            logger,
            cancellationToken);

    public static async Task<VerifiableExecutionEnvelope?> RecordDbEffectAsync(
        this IVerifiableExecutionContext? context,
        string operation,
        string target,
        string? subject,
        Func<Task> effect,
        IReadOnlyDictionary<string, string?>? metadata = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        var result = await context.RecordExternalEffectAsync(
            ExecutionRecordKind.ExternalDbEffect,
            new VerifiableExecutionEffectOptions
            {
                Operation = operation,
                Target = target,
                Subject = subject,
                ComponentType = "db",
                Metadata = metadata
            },
            async () =>
            {
                await effect();
                return true;
            },
            logger,
            cancellationToken);

        return result.Envelope;
    }

    public static Task<VerifiableExecutionEffectResult<T>> RecordHttpCallAsync<T>(
        this IVerifiableExecutionContext? context,
        string operation,
        string method,
        Uri url,
        Func<Task<T>> call,
        IReadOnlyDictionary<string, string?>? metadata = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
        => context.RecordExternalEffectAsync(
            ExecutionRecordKind.ExternalHttpCall,
            new VerifiableExecutionEffectOptions
            {
                Operation = operation,
                Target = SanitizeUri(url),
                Subject = SanitizeUri(url),
                ComponentType = "http",
                Method = method,
                Metadata = metadata
            },
            call,
            logger,
            cancellationToken);

    public static Task<VerifiableExecutionEffectResult<T>> RecordHttpCallAsync<T>(
        this IVerifiableExecutionContext? context,
        string operation,
        string method,
        string url,
        Func<Task<T>> call,
        IReadOnlyDictionary<string, string?>? metadata = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
        => context.RecordHttpCallAsync(operation, method, new Uri(url, UriKind.RelativeOrAbsolute), call, metadata, logger, cancellationToken);

    public static Task<VerifiableExecutionEffectResult<T>> RecordStorageEffectAsync<T>(
        this IVerifiableExecutionContext? context,
        string operation,
        string target,
        string? subject,
        Func<Task<T>> effect,
        IReadOnlyDictionary<string, string?>? metadata = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
        => context.RecordExternalEffectAsync(
            ExecutionRecordKind.ExternalStorageEffect,
            new VerifiableExecutionEffectOptions
            {
                Operation = operation,
                Target = target,
                Subject = subject,
                ComponentType = "storage",
                Metadata = metadata
            },
            effect,
            logger,
            cancellationToken);

    public static Task<VerifiableExecutionEffectResult<T>> RecordLibraryCallAsync<T>(
        this IVerifiableExecutionContext? context,
        string operation,
        string componentName,
        string method,
        Func<Task<T>> call,
        IReadOnlyDictionary<string, string?>? metadata = null,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
        => context.RecordExternalEffectAsync(
            ExecutionRecordKind.ExternalLibraryCall,
            new VerifiableExecutionEffectOptions
            {
                Operation = operation,
                Target = componentName,
                Subject = method,
                ComponentType = "library",
                ComponentName = componentName,
                Method = method,
                Metadata = metadata
            },
            call,
            logger,
            cancellationToken);

    public static async Task<VerifiableExecutionEffectResult<T>> RecordExternalEffectAsync<T>(
        this IVerifiableExecutionContext? context,
        ExecutionRecordKind kind,
        VerifiableExecutionEffectOptions options,
        Func<Task<T>> effect,
        ILogger? logger = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(effect);

        var sw = Stopwatch.StartNew();
        try
        {
            var value = await effect();
            sw.Stop();

            var envelope = await TryRecordAsync(
                context,
                kind,
                options,
                BuildMetadata(options, sw.ElapsedMilliseconds, "success", null, value),
                logger,
                cancellationToken);

            return new VerifiableExecutionEffectResult<T>
            {
                Value = value,
                Envelope = envelope
            };
        }
        catch (Exception ex)
        {
            sw.Stop();
            await TryRecordAsync(
                context,
                kind,
                options,
                BuildMetadata(options, sw.ElapsedMilliseconds, "failure", ex, default(T)),
                logger,
                cancellationToken);

            throw;
        }
    }

    internal static Task<VerifiableExecutionEnvelope?> RecordToolInvocationAsync(
        this IVerifiableExecutionContext? context,
        ExecutionRecordKind kind,
        string alias,
        string method,
        IReadOnlyDictionary<string, object?> arguments,
        object? result,
        long durationMs,
        Exception? error,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["operation"] = method,
            ["target"] = alias,
            ["subject"] = method,
            ["component_type"] = kind == ExecutionRecordKind.PluginCall ? "plugin" : "tool",
            ["component_name"] = alias,
            ["method"] = method,
            ["duration_ms"] = durationMs.ToString(),
            ["status"] = error is null ? "success" : "failure",
            ["argument_hash"] = VerifiableExecutionHash.HashObject(arguments),
            ["result_hash"] = error is null ? HashResult(result) : null,
            ["error_type"] = error?.GetType().FullName,
            ["error_message"] = error?.Message
        };

        return TryRecordAsync(
            context,
            kind,
            new VerifiableExecutionEffectOptions
            {
                Operation = method,
                Target = alias,
                Subject = method,
                ComponentType = kind == ExecutionRecordKind.PluginCall ? "plugin" : "tool",
                ComponentName = alias,
                Method = method
            },
            metadata,
            logger,
            cancellationToken);
    }

    private static async Task<VerifiableExecutionEnvelope?> TryRecordAsync(
        IVerifiableExecutionContext? context,
        ExecutionRecordKind kind,
        VerifiableExecutionEffectOptions options,
        IReadOnlyDictionary<string, string?> metadata,
        ILogger? logger,
        CancellationToken cancellationToken)
    {
        if (context is null)
            return null;

        try
        {
            return await context.RecordExternalEffectAsync(
                kind,
                options.Subject ?? options.Target ?? options.Operation ?? kind.ToString(),
                metadata,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Failed to record verifiable execution evidence for {Kind} {Operation}", kind, options.Operation);
            return null;
        }
    }

    private static Dictionary<string, string?> BuildMetadata<T>(
        VerifiableExecutionEffectOptions options,
        long durationMs,
        string status,
        Exception? error,
        T value)
    {
        var metadata = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["operation"] = options.Operation,
            ["target"] = options.Target,
            ["subject"] = options.Subject,
            ["component_type"] = options.ComponentType,
            ["component_name"] = options.ComponentName,
            ["method"] = options.Method,
            ["duration_ms"] = durationMs.ToString(),
            ["status"] = status,
            ["payload_hash"] = options.PayloadHash,
            ["result_hash"] = error is null ? options.ResultHash ?? HashResult(value) : null,
            ["error_type"] = error?.GetType().FullName,
            ["error_message"] = error?.Message
        };

        if (options.Metadata is not null)
        {
            foreach (var kvp in options.Metadata)
                metadata[kvp.Key] = kvp.Value;
        }

        if (value is HttpResponseMessage response)
        {
            metadata["status_code"] = ((int)response.StatusCode).ToString();
            metadata["http_success"] = response.IsSuccessStatusCode.ToString();
        }

        return metadata;
    }

    private static string? HashResult<T>(T value)
    {
        if (value is null)
            return null;

        if (value is HttpResponseMessage response)
            return VerifiableExecutionHash.HashText(((int)response.StatusCode).ToString());

        return VerifiableExecutionHash.HashObject(value);
    }

    private static string SanitizeUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri)
            return uri.GetComponents(UriComponents.Path, UriFormat.SafeUnescaped);

        var builder = new UriBuilder(uri)
        {
            Query = string.Empty,
            Fragment = string.Empty,
            UserName = string.Empty,
            Password = string.Empty
        };

        return builder.Uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.SafeUnescaped);
    }
}
