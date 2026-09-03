using LiveViewEngine.Core;

namespace ViewEngineServer.WebApp.WebSocket;

public interface IOutboundProtocolEncoder
{
    OutboundMessageFormat Format { get; }
    byte[] EncodeSubscriptionAccepted(SubscriptionAcceptedPayload payload);
    byte[] EncodeSubscriptionRejected(SubscriptionRejectedPayload payload);
    // Non-terminal counterpart to EncodeSubscriptionRejected: sent when an updateview/setviewport
    // request is rejected (e.g. a disabled capability) but the subscription itself stays alive with
    // its previous view untouched. Must use a distinct wire shape/type so clients don't confuse it
    // with a terminal subscribe rejection and tear down a subscription the server kept active.
    byte[] EncodeUpdateRejected(SubscriptionRejectedPayload payload);
    IEnumerable<byte[]> EncodeFrames(ViewDelta delta, int subscriptionId);
}
