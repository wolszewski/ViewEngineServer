using LiveViewEngine.TcpProtocol;

namespace LiveViewEngine.TcpClient;

public sealed record TcpCollectionSchemaSnapshot(
    string CollectionName,
    IReadOnlyList<TcpSchemaField> Fields);

public interface ILiveViewEngineTcpClient
{
    Task<bool> CreateCollectionAsync(
        string collectionName,
        IReadOnlyList<string> fieldNames,
        IReadOnlyList<string>? fieldTypes = null,
        CancellationToken cancellationToken = default);

    Task<TcpCollectionSchemaSnapshot?> GetSchemaAsync(
        string collectionName,
        CancellationToken cancellationToken = default);

    Task<bool> IngestAsync(
        string collectionName,
        string rowKey,
        IReadOnlyDictionary<string, string?> fieldValues,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string collectionName,
        string rowKey,
        CancellationToken cancellationToken = default);
}
