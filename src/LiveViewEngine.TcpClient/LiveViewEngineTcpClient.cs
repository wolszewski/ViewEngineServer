using System.Collections.Concurrent;
using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using AckResponseMessage = global::LiveViewEngine.TcpProtocol.AckResponseMessage;
using ConnectedResponseMessage = global::LiveViewEngine.TcpProtocol.ConnectedResponseMessage;
using CreateCollectionRequestMessage = global::LiveViewEngine.TcpProtocol.CreateCollectionRequestMessage;
using DeleteRequestMessage = global::LiveViewEngine.TcpProtocol.DeleteRequestMessage;
using ErrorResponseMessage = global::LiveViewEngine.TcpProtocol.ErrorResponseMessage;
using GetSchemaRequestMessage = global::LiveViewEngine.TcpProtocol.GetSchemaRequestMessage;
using PongResponseMessage = global::LiveViewEngine.TcpProtocol.PongResponseMessage;
using SchemaResponseMessage = global::LiveViewEngine.TcpProtocol.SchemaResponseMessage;
using SocketTcpClient = System.Net.Sockets.TcpClient;
using TcpProtocolCodec = global::LiveViewEngine.TcpProtocol.TcpProtocolCodec;
using TcpRequestMessage = global::LiveViewEngine.TcpProtocol.TcpRequestMessage;
using TcpResponseMessage = global::LiveViewEngine.TcpProtocol.TcpResponseMessage;
using TcpSchemaField = global::LiveViewEngine.TcpProtocol.TcpSchemaField;
using UpsertRequestMessage = global::LiveViewEngine.TcpProtocol.UpsertRequestMessage;

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

public sealed class LiveViewEngineTcpClient(
    LiveViewEngineTcpClientOptions options,
    ILogger<LiveViewEngineTcpClient> logger) : ILiveViewEngineTcpClient
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    private readonly Channel<QueuedRequest> _queue = Channel.CreateBounded<QueuedRequest>(new BoundedChannelOptions(options.QueueCapacity)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<long, QueuedRequest> _pending = new();
    private readonly ConcurrentDictionary<string, CachedCollectionSchema> _schemaCache =
        new(StringComparer.OrdinalIgnoreCase);
    private long _nextRequestId;

    public async Task RunAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var client = new SocketTcpClient();
                client.NoDelay = true;
                await client.ConnectAsync(options.Host, options.Port, stoppingToken).ConfigureAwait(false);
                logger.LogInformation("Connected TCP ingestion client to {Host}:{Port}.", options.Host, options.Port);

                await using var stream = client.GetStream();
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                var connectedTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var receiveTask = ReceiveLoopAsync(stream, linkedCts.Token, connectedTcs);
                try
                {
                    await connectedTcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    var sendTask = SendLoopAsync(stream, linkedCts.Token);
                    var completedTask = await Task.WhenAny(sendTask, receiveTask).ConfigureAwait(false);
                    linkedCts.Cancel();
                    await completedTask.ConfigureAwait(false);
                    await Task.WhenAll(sendTask, receiveTask).ConfigureAwait(false);
                }
                catch
                {
                    linkedCts.Cancel();

                    try
                    {
                        await receiveTask.ConfigureAwait(false);
                    }
                    catch
                    {
                    }

                    throw;
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                FailPendingRequests(ex);
                _schemaCache.Clear();
                logger.LogWarning(ex, "TCP ingestion client disconnected from {Host}:{Port}.", options.Host, options.Port);

                try
                {
                    await Task.Delay(options.ReconnectDelay, stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        FailPendingRequests(new OperationCanceledException("TCP ingestion client stopped."));
    }

    public async Task<bool> CreateCollectionAsync(
        string collectionName,
        IReadOnlyList<string> fieldNames,
        IReadOnlyList<string>? fieldTypes = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
            ArgumentNullException.ThrowIfNull(fieldNames);

            if (fieldNames.Count == 0)
            {
                throw new ArgumentException("At least one field name must be provided.", nameof(fieldNames));
            }

            var resolvedFieldTypes = ResolveFieldTypes(fieldNames, fieldTypes);
            var fields = new TcpSchemaField[fieldNames.Count];
            for (var index = 0; index < fieldNames.Count; index++)
            {
                fields[index] = new TcpSchemaField(index + 1, fieldNames[index], resolvedFieldTypes[index]);
            }

            var response = await SendAsync<SchemaResponseMessage>(
                new CreateCollectionRequestMessage(NextRequestId(), collectionName, fields),
                cancellationToken).ConfigureAwait(false);
            CacheSchema(response);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TCP create collection failed for '{CollectionName}'.", collectionName);
            return false;
        }
    }

    public async Task<TcpCollectionSchemaSnapshot?> GetSchemaAsync(
        string collectionName,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);

            if (_schemaCache.TryGetValue(collectionName, out var cached))
            {
                return cached.ToSnapshot();
            }

            var response = await SendAsync<SchemaResponseMessage>(
                new GetSchemaRequestMessage(NextRequestId(), collectionName),
                cancellationToken).ConfigureAwait(false);
            return CacheSchema(response).ToSnapshot();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TCP get schema failed for '{CollectionName}'.", collectionName);
            return null;
        }
    }

    public async Task<bool> IngestAsync(
        string collectionName,
        string rowKey,
        IReadOnlyDictionary<string, string?> fieldValues,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
            ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);
            ArgumentNullException.ThrowIfNull(fieldValues);

            var schema = await GetOrLoadSchemaAsync(collectionName, cancellationToken).ConfigureAwait(false);
            if (schema is null)
            {
                return false;
            }

            var fields = new List<KeyValuePair<int, string?>>(fieldValues.Count);
            foreach (var (fieldName, value) in fieldValues)
            {
                if (!schema.TryGetFieldIndex(fieldName, out var fieldIndex) || fieldIndex <= 0)
                {
                    throw new InvalidOperationException(
                        $"Field '{fieldName}' does not exist in collection '{collectionName}'.");
                }

                fields.Add(new KeyValuePair<int, string?>(fieldIndex, value));
            }

            await SendFireAndForgetAsync(
                new UpsertRequestMessage(NextRequestId(), collectionName, rowKey, fields),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TCP ingest failed for '{CollectionName}' row '{RowKey}'.", collectionName, rowKey);
            return false;
        }
    }

    public async Task<bool> DeleteAsync(
        string collectionName,
        string rowKey,
        CancellationToken cancellationToken = default)
    {
        try
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(collectionName);
            ArgumentException.ThrowIfNullOrWhiteSpace(rowKey);

            await SendFireAndForgetAsync(
                new DeleteRequestMessage(NextRequestId(), collectionName, rowKey),
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TCP delete failed for '{CollectionName}' row '{RowKey}'.", collectionName, rowKey);
            return false;
        }
    }

    private async Task SendLoopAsync(NetworkStream stream, CancellationToken ct)
    {
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        try
        {
            await foreach (var queued in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                if (queued.ExpectsResponse)
                {
                    _pending[queued.Request.RequestId] = queued;
                }

                TcpProtocolCodec.WriteRequestLine(writer, queued.Request);
                await writer.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task ReceiveLoopAsync(
        NetworkStream stream,
        CancellationToken ct,
        TaskCompletionSource? connectedTcs = null)
    {
        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        var connected = connectedTcs is null;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (TryReadLine(ref buffer, out var line))
                {
                    var response = TcpProtocolCodec.ParseResponse(DecodeLine(line));
                    if (!connected && response is not ConnectedResponseMessage)
                    {
                        throw new InvalidOperationException(
                            $"Expected initial response '{nameof(ConnectedResponseMessage)}' but received '{response.GetType().Name}'.");
                    }

                    await HandleResponseAsync(response).ConfigureAwait(false);
                    if (!connected)
                    {
                        connected = true;
                        connectedTcs!.TrySetResult();
                    }
                }

                reader.AdvanceTo(buffer.Start, buffer.End);
                if (result.IsCompleted)
                {
                    throw new IOException(connected
                        ? "TCP ingestion server closed the connection."
                        : "TCP ingestion server closed the connection before sending CONNECTED.");
                }
            }
        }
        catch (Exception ex)
        {
            connectedTcs?.TrySetException(ex);
            throw;
        }
        finally
        {
            if (ct.IsCancellationRequested)
            {
                connectedTcs?.TrySetCanceled(ct);
            }

            await reader.CompleteAsync().ConfigureAwait(false);
        }
    }

    private Task HandleResponseAsync(TcpResponseMessage response)
    {
        switch (response)
        {
            case ConnectedResponseMessage connected:
                if (connected.ProtocolVersion != TcpProtocolCodec.ProtocolVersion)
                {
                    throw new InvalidOperationException(
                        $"Unsupported TCP protocol version '{connected.ProtocolVersion}'.");
                }

                logger.LogInformation("TCP ingestion server protocol version {ProtocolVersion}.", connected.ProtocolVersion);
                return Task.CompletedTask;
            case SchemaResponseMessage schema:
                CompletePending(schema.RequestId, schema);
                return Task.CompletedTask;
            case AckResponseMessage:
                return Task.CompletedTask;
            case ErrorResponseMessage error:
                if (_pending.ContainsKey(error.RequestId))
                {
                    FailPending(error.RequestId, new InvalidOperationException(error.Message));
                }
                else
                {
                    logger.LogWarning(
                        "Asynchronous TCP ingest error for request {RequestId}: {Message}",
                        error.RequestId,
                        error.Message);
                }

                return Task.CompletedTask;
            case PongResponseMessage pong:
                CompletePending(pong.RequestId, pong);
                return Task.CompletedTask;
            default:
                throw new InvalidOperationException($"Unsupported response type '{response.GetType().Name}'.");
        }
    }

    private CachedCollectionSchema CacheSchema(SchemaResponseMessage schema)
    {
        var cached = new CachedCollectionSchema(schema.CollectionName, schema.Fields);
        _schemaCache[schema.CollectionName] = cached;
        return cached;
    }

    private async Task<CachedCollectionSchema?> GetOrLoadSchemaAsync(
        string collectionName,
        CancellationToken cancellationToken)
    {
        if (_schemaCache.TryGetValue(collectionName, out var cached))
        {
            return cached;
        }

        var schema = await GetSchemaAsync(collectionName, cancellationToken).ConfigureAwait(false);
        if (schema is null)
        {
            return null;
        }

        return _schemaCache.TryGetValue(collectionName, out cached) ? cached : null;
    }

    private async Task<TResponse> SendAsync<TResponse>(
        TcpRequestMessage request,
        CancellationToken cancellationToken)
        where TResponse : TcpResponseMessage
    {
        var queued = new QueuedRequest(request, expectsResponse: true);
        await _queue.Writer.WriteAsync(queued, cancellationToken).ConfigureAwait(false);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(options.RequestTimeout);
        TcpResponseMessage response;
        try
        {
            response = await queued.Completion!.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"TCP request '{request.RequestId}' timed out.", ex);
        }
        if (response is TResponse typedResponse)
        {
            return typedResponse;
        }

        throw new InvalidOperationException(
            $"Expected response '{typeof(TResponse).Name}' but received '{response.GetType().Name}'.");
    }

    private async Task SendFireAndForgetAsync(TcpRequestMessage request, CancellationToken cancellationToken)
    {
        var queued = new QueuedRequest(request, expectsResponse: false);
        await _queue.Writer.WriteAsync(queued, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryReadLine(ref ReadOnlySequence<byte> buffer, out ReadOnlySequence<byte> line)
    {
        var position = buffer.PositionOf((byte)'\n');
        if (position is null)
        {
            line = default;
            return false;
        }

        line = buffer.Slice(0, position.Value);
        buffer = buffer.Slice(buffer.GetPosition(1, position.Value));
        return true;
    }

    private static string DecodeLine(ReadOnlySequence<byte> line)
    {
        if (line.IsSingleSegment)
        {
            return Utf8.GetString(line.FirstSpan);
        }

        var bytes = line.ToArray();
        return Utf8.GetString(bytes);
    }

    private void CompletePending(long requestId, TcpResponseMessage response)
    {
        if (_pending.TryRemove(requestId, out var queued))
        {
            queued.Completion!.TrySetResult(response);
        }
    }

    private void FailPending(long requestId, Exception exception)
    {
        if (_pending.TryRemove(requestId, out var queued))
        {
            queued.Completion!.TrySetException(exception);
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var pending in _pending.Keys.ToArray())
        {
            FailPending(pending, exception);
        }
    }

    private long NextRequestId() => Interlocked.Increment(ref _nextRequestId);

    private static string[] ResolveFieldTypes(IReadOnlyList<string> fieldNames, IReadOnlyList<string>? fieldTypes)
    {
        if (fieldTypes is null || fieldTypes.Count == 0)
        {
            return Enumerable.Repeat("string", fieldNames.Count).ToArray();
        }

        if (fieldTypes.Count != fieldNames.Count)
        {
            throw new ArgumentException("Field type count must match the field name count.", nameof(fieldTypes));
        }

        return fieldTypes.ToArray();
    }

    private sealed class QueuedRequest
    {
        public QueuedRequest(TcpRequestMessage request, bool expectsResponse)
        {
            Request = request;
            ExpectsResponse = expectsResponse;
            Completion = expectsResponse
                ? new TaskCompletionSource<TcpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously)
                : null;
        }

        public TcpRequestMessage Request { get; }
        public bool ExpectsResponse { get; }
        public TaskCompletionSource<TcpResponseMessage>? Completion { get; }
    }

    private sealed class CachedCollectionSchema
    {
        private readonly IReadOnlyDictionary<string, int> _fieldIndexes;

        public CachedCollectionSchema(string collectionName, IReadOnlyList<TcpSchemaField> fields)
        {
            CollectionName = collectionName;
            Fields = fields.ToArray();
            _fieldIndexes = Fields.ToDictionary(field => field.Name, field => field.Index, StringComparer.OrdinalIgnoreCase);
        }

        public string CollectionName { get; }
        public IReadOnlyList<TcpSchemaField> Fields { get; }

        public bool TryGetFieldIndex(string fieldName, out int fieldIndex) => _fieldIndexes.TryGetValue(fieldName, out fieldIndex);

        public TcpCollectionSchemaSnapshot ToSnapshot() => new(CollectionName, Fields);
    }
}
