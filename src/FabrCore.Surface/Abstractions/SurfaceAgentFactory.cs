using Microsoft.Agents.AI;

namespace FabrCore.Surface.Abstractions;

/// <summary>
/// Host-supplied factory for creating a planning <see cref="AIAgent"/> by model config name.
/// </summary>
public delegate Task<AIAgent> SurfaceAgentFactory(string modelName, CancellationToken cancellationToken);
