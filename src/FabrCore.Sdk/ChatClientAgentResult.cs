using Microsoft.Agents.AI;

namespace FabrCore.Sdk;

/// <summary>
/// Result from CreateChatClientAgent containing the configured agent and its conversation session.
/// </summary>
/// <param name="Agent">The configured ChatClientAgent instance.</param>
/// <param name="Session">The conversation session for maintaining message history.</param>
public record ChatClientAgentResult(AIAgent Agent, AgentSession Session, FabrCoreChatHistoryProvider? ChatHistoryProvider = null);
