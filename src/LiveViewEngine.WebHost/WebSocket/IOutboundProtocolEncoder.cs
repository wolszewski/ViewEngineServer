using LiveViewEngine.Core;

namespace ViewEngineServer.WebApp.WebSocket;

public interface IOutboundProtocolEncoder
{
    OutboundMessageFormat Format { get; }
    byte[] EncodeSubscriptionAccepted(SubscriptionAcceptedPayload payload);
    IEnumerable<byte[]> EncodeFrames(ViewDelta delta, int subscriptionId);
}
