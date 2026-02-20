using Fabr.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace Fabr.Sdk
{

    public class A2AAgentSession : InMemoryAgentSession
    {
        internal A2AAgentSession() : base() { }
        internal A2AAgentSession(JsonElement serializedSessionState, JsonSerializerOptions? jsonSerializerOptions = null)
            : base(serializedSessionState, jsonSerializerOptions) { }
    }

    public class A2AAgentProxy : AIAgent
    {
        private readonly IFabrAgentHost fabrAgentHost;
        private readonly string handle;

        public A2AAgentProxy(IFabrAgentHost fabrAgentHost, string handle)
        {
            this.fabrAgentHost = fabrAgentHost;
            this.handle = handle;
        }

        public override ValueTask<AgentSession> GetNewSessionAsync(CancellationToken cancellationToken = default)
        {
            var session = new A2AAgentSession();
            return ValueTask.FromResult<AgentSession>(session);
        }

        public override ValueTask<AgentSession> DeserializeSessionAsync(JsonElement serializedSession, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        {
            var session = new A2AAgentSession(serializedSession, jsonSerializerOptions);
            return ValueTask.FromResult<AgentSession>(session);
        }

        protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            var message = new AgentMessage
            {
                ToHandle = handle,
                Message = string.Join("\r\n", messages.Select(m => m.Text))
            };
            var response = await fabrAgentHost.SendAndReceiveMessage(message);
            var responseMessages = new List<ChatMessage>();
            responseMessages.Add(new ChatMessage(ChatRole.Assistant, response.Message));

            return new AgentResponse
            {
                Messages = responseMessages
            };
        }

        protected override async IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            foreach (var m in messages)
            {
                var message = new AgentMessage
                {
                    ToHandle = handle,
                    Message = string.Join("\r\n", messages.Select(m => m.Text))
                };
                var response = await fabrAgentHost.SendAndReceiveMessage(message);
                var update = new AgentResponseUpdate(ChatRole.Assistant, response.Message);
                yield return update;
            }
        }
    }

    // Keep old names as aliases for backward compatibility during migration
    public class A2AAgentThread : A2AAgentSession
    {
        internal A2AAgentThread() : base() { }
        internal A2AAgentThread(JsonElement serializedThreadState, JsonSerializerOptions? jsonSerializerOptions = null)
            : base(serializedThreadState, jsonSerializerOptions) { }
    }
}
