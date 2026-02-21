using FabrCore.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.Text.Json;

namespace FabrCore.Sdk
{

    public class A2AAgentSession : AgentSession
    {
        internal A2AAgentSession() : base() { }
    }

    public class A2AAgentProxy : AIAgent
    {
        private readonly IFabrCoreAgentHost fabrcoreAgentHost;
        private readonly string handle;

        public A2AAgentProxy(IFabrCoreAgentHost fabrcoreAgentHost, string handle)
        {
            this.fabrcoreAgentHost = fabrcoreAgentHost;
            this.handle = handle;
        }

        protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<AgentSession>(new A2AAgentSession());
        }

        protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult(JsonSerializer.SerializeToElement(session.StateBag, jsonSerializerOptions));
        }

        protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedState, JsonSerializerOptions? jsonSerializerOptions = null, CancellationToken cancellationToken = default)
        {
            return ValueTask.FromResult<AgentSession>(new A2AAgentSession());
        }

        protected override async Task<AgentResponse> RunCoreAsync(IEnumerable<ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        {
            var message = new AgentMessage
            {
                ToHandle = handle,
                Message = string.Join("\r\n", messages.Select(m => m.Text))
            };
            var response = await fabrcoreAgentHost.SendAndReceiveMessage(message);
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
                var response = await fabrcoreAgentHost.SendAndReceiveMessage(message);
                var update = new AgentResponseUpdate(ChatRole.Assistant, response.Message);
                yield return update;
            }
        }
    }
}
