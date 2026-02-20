using Orleans;

namespace Fabr.Core.Interfaces
{
    public interface IClientGrainObserver : IGrainObserver
    {
        void OnMessageReceived(AgentMessage message);
    }
}
