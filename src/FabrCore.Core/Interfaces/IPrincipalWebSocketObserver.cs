using FabrCore.Core.WebSockets;
using Orleans;

namespace FabrCore.Core.Interfaces;

public interface IPrincipalWebSocketObserver : IGrainObserver
{
    void OnDelivery(FabrCoreWebSocketDeliveryRecord delivery);
}
