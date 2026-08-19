namespace LiveViewEngine.TcpProtocol;

public sealed record TcpSchemaField(int Index, string Name, string Type);

public abstract record TcpRequestMessage(long RequestId);

public sealed record CreateCollectionRequestMessage(
    long RequestId,
    string CollectionName,
    IReadOnlyList<TcpSchemaField> Fields) : TcpRequestMessage(RequestId);

public sealed record GetSchemaRequestMessage(long RequestId, string CollectionName) : TcpRequestMessage(RequestId);

public sealed record UpsertRequestMessage(
    long RequestId,
    string CollectionName,
    string RowKey,
    IReadOnlyList<KeyValuePair<int, string?>> Fields) : TcpRequestMessage(RequestId);

public sealed record DeleteRequestMessage(long RequestId, string CollectionName, string RowKey) : TcpRequestMessage(RequestId);

public sealed record PingRequestMessage(long RequestId) : TcpRequestMessage(RequestId);

public abstract record TcpResponseMessage;

public sealed record ConnectedResponseMessage(int ProtocolVersion) : TcpResponseMessage;

public sealed record AckResponseMessage(long RequestId, string Operation) : TcpResponseMessage;

public sealed record ErrorResponseMessage(long RequestId, string Message) : TcpResponseMessage;

public sealed record SchemaResponseMessage(
    long RequestId,
    string CollectionName,
    IReadOnlyList<TcpSchemaField> Fields) : TcpResponseMessage;

public sealed record PongResponseMessage(long RequestId) : TcpResponseMessage;
