using FabrCore.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FabrCore.Client
{
    /// <summary>
    /// Base class for agent components that can be dropped onto a parent Blazor component.
    /// Inherit from this class and specify the agent proxy type and parent component type.
    /// </summary>
    /// <typeparam name="TAgent">The agent proxy type that inherits from FabrCoreClientAgentProxy.</typeparam>
    /// <typeparam name="TComponent">The parent Blazor component type.</typeparam>
    public abstract class FabrCoreClientAgent<TAgent, TComponent> : ComponentBase, IAsyncDisposable
        where TAgent : FabrCoreClientAgentProxy<TComponent>
        where TComponent : ComponentBase
    {
        private ILogger? _logger;

        [Inject]
        protected IClientContextFactory ClientContextFactory { get; set; } = null!;

        [Inject]
        protected IServiceProvider ServiceProvider { get; set; } = null!;

        [Inject]
        protected IFabrCoreHostApiClient FabrCoreHostApiClient { get; set; } = null!;

        [Inject]
        protected ILoggerFactory LoggerFactory { get; set; } = null!;

        /// <summary>
        /// The parent component this agent is attached to.
        /// </summary>
        [Parameter, EditorRequired]
        public TComponent Component { get; set; } = null!;

        /// <summary>
        /// The unique handle for this agent in the cluster.
        /// </summary>
        [Parameter, EditorRequired]
        public string Handle { get; set; } = null!;

        /// <summary>
        /// Gets the agent proxy instance. Available after OnInitializedAsync completes.
        /// </summary>
        public TAgent? Agent { get; private set; }

        /// <summary>
        /// Gets whether the agent has been initialized.
        /// </summary>
        public bool IsInitialized => Agent?.IsInitialized ?? false;

        protected override async Task OnInitializedAsync()
        {
            _logger = LoggerFactory.CreateLogger(GetType());
            _logger.LogDebug("FabrCoreClientAgent OnInitializedAsync starting - Handle: {Handle}, ComponentType: {ComponentType}",
                Handle, typeof(TComponent).Name);

            if (Component == null)
            {
                _logger.LogError("FabrCoreClientAgent initialization failed - Component parameter is null. Handle: {Handle}", Handle);
                throw new InvalidOperationException("Component parameter is required.");
            }

            if (string.IsNullOrEmpty(Handle))
            {
                _logger.LogError("FabrCoreClientAgent initialization failed - Handle parameter is null or empty");
                throw new InvalidOperationException("Handle parameter is required.");
            }

            try
            {
                _logger.LogDebug("Creating agent proxy - Handle: {Handle}, AgentType: {AgentType}",
                    Handle, typeof(TAgent).Name);

                Agent = CreateAgent();

                _logger.LogDebug("Agent proxy created, initializing - Handle: {Handle}", Handle);

                await Agent.InitializeAsync();

                _logger.LogInformation("FabrCoreClientAgent initialized successfully - Handle: {Handle}, AgentType: {AgentType}",
                    Handle, typeof(TAgent).Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "FabrCoreClientAgent initialization failed - Handle: {Handle}, AgentType: {AgentType}",
                    Handle, typeof(TAgent).Name);
                throw;
            }
        }

        /// <summary>
        /// Creates the agent proxy instance. Override to customize agent creation.
        /// </summary>
        protected abstract TAgent CreateAgent();

        public virtual async ValueTask DisposeAsync()
        {
            _logger?.LogDebug("FabrCoreClientAgent disposing - Handle: {Handle}", Handle);

            if (Agent != null)
            {
                try
                {
                    await Agent.DisposeAsync();
                    _logger?.LogInformation("FabrCoreClientAgent disposed successfully - Handle: {Handle}", Handle);
                }
                catch (Exception ex)
                {
                    _logger?.LogError(ex, "Error disposing FabrCoreClientAgent - Handle: {Handle}", Handle);
                }
                Agent = null;
            }
            GC.SuppressFinalize(this);
        }
    }
}
