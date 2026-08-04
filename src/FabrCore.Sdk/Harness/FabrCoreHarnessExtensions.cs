using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace FabrCore.Sdk;

/// <summary>
/// Extension methods for assembling a <see cref="FabrCoreHarnessAgent"/> from an <see cref="IChatClient"/>.
/// </summary>
public static class FabrCoreHarnessExtensions
{
    /// <summary>
    /// Assembles a FabrCore harness agent — todos, an iteration loop, and background delegation — over this
    /// chat client.
    /// </summary>
    /// <remarks>
    /// This is the pure assembler and needs no FabrCore context, which is what makes it usable outside an
    /// agent grain. Inside a <see cref="FabrCoreAgentProxy"/>, prefer
    /// <c>CreateFabrCoreHarnessAgent</c>: it supplies the tracked chat client, the Orleans-backed history
    /// provider, config-driven <c>_Harness*</c> settings, and durable session snapshots, then calls this.
    /// </remarks>
    /// <param name="chatClient">The chat client the agent talks to.</param>
    /// <param name="options">Composition options. <see langword="null"/> yields a todo-enabled, single-shot agent.</param>
    /// <param name="loggerFactory">Optional logger factory.</param>
    /// <param name="services">Optional service provider used when building the agent pipeline.</param>
    /// <returns>The assembled harness agent.</returns>
    public static FabrCoreHarnessAgent AsFabrCoreHarnessAgent(
        this IChatClient chatClient,
        FabrCoreHarnessOptions? options = null,
        ILoggerFactory? loggerFactory = null,
        IServiceProvider? services = null) =>
        new(chatClient, options, loggerFactory, services);
}
