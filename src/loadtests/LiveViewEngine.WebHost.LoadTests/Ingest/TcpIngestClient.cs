using LiveViewEngine.TcpClient;

namespace LiveViewEngine.WebHost.LoadTests.Ingest;

/// <summary>
/// Ingest via the persistent TCP protocol. IngestAsync is fire-and-forget on the wire (the
/// server acks asynchronously); it only confirms the request was accepted for send, not that
/// the server has applied it yet - the delta_latency scenario's WS-side wait is what actually
/// confirms application.
/// </summary>
public sealed class TcpIngestClient(ILiveViewEngineTcpClient client) : IIngestClient
{
    public Task<bool> CreateCollectionAsync(
        string collectionName,
        IReadOnlyList<string> fieldNames,
        IReadOnlyList<string>? fieldTypes = null,
        CancellationToken ct = default) =>
        client.CreateCollectionAsync(collectionName, fieldNames, fieldTypes, ct);

    public Task<bool> IngestAsync(
        string collectionName,
        string primaryKeyValue,
        IReadOnlyDictionary<string, string?> fields,
        CancellationToken ct = default) =>
        client.IngestAsync(collectionName, primaryKeyValue, fields, ct);
}
