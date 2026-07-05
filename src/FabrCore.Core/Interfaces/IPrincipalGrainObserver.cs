using Orleans;

namespace FabrCore.Core.Interfaces
{
    public interface IPrincipalGrainObserver : IGrainObserver
    {
        void OnMessageReceived(AgentMessage message);
    }
}
