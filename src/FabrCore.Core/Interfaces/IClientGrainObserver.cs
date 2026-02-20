using Orleans;

namespace FabrCore.Core.Interfaces
{
    public interface IClientGrainObserver : IGrainObserver
    {
        void OnMessageReceived(AgentMessage message);
    }
}
