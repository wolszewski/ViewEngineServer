using System.Collections.Concurrent;
using System.Globalization;
using System.Threading.Channels;
using LiveViewEngine.Core;
using LiveViewEngine.Core.Data;
using LiveViewEngine.TcpProtocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ViewEngineServer.WebApp.Tcp;

public sealed class TcpIngestRequestDispatcher(
    IViewEngine engine,
    ICollectionStore store,
    TcpIngestOptions options,
    IHostApplicationLifetime applicationLifetime,
    ILogger<TcpIngestRequestDispatcher> logger)
{
    private readonly ConcurrentDictionary<string, CollectionIngestQueue> _collectionQueues =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<TcpResponseMessage?> DispatchAsync(TcpRequestMessage request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);

        try
        {
            return request switch
            {
                CreateCollectionRequestMessage create => await HandleCreateCollectionAsync(create, ct).ConfigureAwait(false),
                GetSchemaRequestMessage getSchema => HandleGetSchema(getSchema),
                UpsertRequestMessage upsert => await HandleUpsertAsync(upsert, ct).ConfigureAwait(false),
                DeleteRequestMessage delete => await HandleDeleteAsync(delete, ct).ConfigureAwait(false),
                PingRequestMessage ping => new PongResponseMessage(ping.RequestId),
                _ => new ErrorResponseMessage(request.RequestId, $"Unsupported request '{request.GetType().Name}'.")
            };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TCP ingest request {RequestType} failed.", request.GetType().Name);
            if (request is UpsertRequestMessage or DeleteRequestMessage)
            {
                return options.EnableAsyncAcks
                    ? new ErrorResponseMessage(request.RequestId, ex.Message)
                    : null;
            }

            return new ErrorResponseMessage(request.RequestId, ex.Message);
        }
    }

    private async Task<TcpResponseMessage> HandleCreateCollectionAsync(
        CreateCollectionRequestMessage request,
        CancellationToken ct)
    {
        if (request.Fields.Count == 0)
        {
            return new ErrorResponseMessage(request.RequestId, "At least one field must be defined.");
        }

        var fieldNames = request.Fields.Select(static field => field.Name).ToList();
        var fieldTypes = request.Fields.Select(field => ParseScalarFieldType(field.Type)).ToList();
        var schema = new CollectionSchema(request.CollectionName, fieldNames, fieldTypes);
        var result = await engine.IngestAsync(
            new CreateCollectionCommand
            {
                CollectionId = request.CollectionName,
                Schema = schema
            },
            ct).ConfigureAwait(false);

        if (!result.Success)
        {
            return new ErrorResponseMessage(request.RequestId, result.Error ?? "Collection creation failed.");
        }

        return CreateSchemaResponse(request.RequestId, schema);
    }

    private TcpResponseMessage HandleGetSchema(GetSchemaRequestMessage request)
    {
        if (!store.TryGetSchema(request.CollectionName, out var schema) || schema is null)
        {
            return new ErrorResponseMessage(request.RequestId, $"Collection '{request.CollectionName}' not found.");
        }

        return CreateSchemaResponse(request.RequestId, schema);
    }

    private async Task<TcpResponseMessage?> HandleUpsertAsync(UpsertRequestMessage request, CancellationToken ct)
    {
        if (!store.TryGetSchema(request.CollectionName, out var schema) || schema is null)
        {
            var message = $"Collection '{request.CollectionName}' not found.";
            logger.LogWarning("TCP upsert rejected: {Message}", message);
            return options.EnableAsyncAcks ? new ErrorResponseMessage(request.RequestId, message) : null;
        }

        var fields = new Dictionary<string, string?>(request.Fields.Count, StringComparer.Ordinal);
        foreach (var (fieldIndex, value) in request.Fields)
        {
            if (fieldIndex <= CollectionSchema.PrimaryKeyIndex || fieldIndex >= schema.Fields.Count)
            {
                var message = $"Field index '{fieldIndex}' is invalid for collection '{request.CollectionName}'.";
                logger.LogWarning("TCP upsert rejected: {Message}", message);
                return options.EnableAsyncAcks ? new ErrorResponseMessage(request.RequestId, message) : null;
            }

            var fieldName = schema.GetFieldDefinition(fieldIndex).Name;
            fields[fieldName] = value;
        }

        await GetOrCreateCollectionQueue(request.CollectionName)
            .EnqueueAsync(new UpsertWorkItem(request.CollectionName, request.RowKey, fields), ct)
            .ConfigureAwait(false);

        return options.EnableAsyncAcks
            ? new AckResponseMessage(request.RequestId, "UPSERT")
            : null;
    }

    private async Task<TcpResponseMessage?> HandleDeleteAsync(DeleteRequestMessage request, CancellationToken ct)
    {
        if (!store.TryGet(request.CollectionName, out _))
        {
            var message = $"Collection '{request.CollectionName}' not found.";
            logger.LogWarning("TCP delete rejected: {Message}", message);
            return options.EnableAsyncAcks ? new ErrorResponseMessage(request.RequestId, message) : null;
        }

        await GetOrCreateCollectionQueue(request.CollectionName)
            .EnqueueAsync(new DeleteWorkItem(request.CollectionName, request.RowKey), ct)
            .ConfigureAwait(false);

        return options.EnableAsyncAcks
            ? new AckResponseMessage(request.RequestId, "DELETE")
            : null;
    }

    private CollectionIngestQueue GetOrCreateCollectionQueue(string collectionName)
    {
        return _collectionQueues.GetOrAdd(
            collectionName,
            name => new CollectionIngestQueue(
                name,
                options.CollectionQueueCapacity,
                engine,
                logger,
                applicationLifetime.ApplicationStopping));
    }

    private static SchemaResponseMessage CreateSchemaResponse(long requestId, CollectionSchema schema)
    {
        var fields = schema.Fields
            .Select(field => new TcpSchemaField(field.FieldIndex, field.Name, MapScalarFieldType(field.Type)))
            .ToArray();
        return new SchemaResponseMessage(requestId, schema.CollectionName, fields);
    }

    private static ScalarFieldType ParseScalarFieldType(string fieldType)
    {
        return fieldType.Trim().ToLowerInvariant() switch
        {
            "string" => ScalarFieldType.String,
            "enum" => ScalarFieldType.String,
            "int" => ScalarFieldType.Int32,
            "int32" => ScalarFieldType.Int32,
            "long" => ScalarFieldType.Int64,
            "int64" => ScalarFieldType.Int64,
            "double" => ScalarFieldType.Double,
            "decimal" => ScalarFieldType.Decimal,
            "dateonly" => ScalarFieldType.DateOnly,
            "datetime" => ScalarFieldType.DateTime,
            "datetimeoffset" => ScalarFieldType.DateTimeOffset,
            _ => throw new FormatException(
                string.Create(
                    CultureInfo.InvariantCulture,
                    $"Unsupported field type '{fieldType}'."))
        };
    }

    private static string MapScalarFieldType(ScalarFieldType fieldType)
    {
        return fieldType switch
        {
            ScalarFieldType.String => "string",
            ScalarFieldType.Int32 => "int",
            ScalarFieldType.Int64 => "long",
            ScalarFieldType.Double => "double",
            ScalarFieldType.Decimal => "decimal",
            ScalarFieldType.DateOnly => "dateonly",
            ScalarFieldType.DateTime => "datetime",
            ScalarFieldType.DateTimeOffset => "datetimeoffset",
            _ => "string"
        };
    }

    private abstract record IngestWorkItem(string CollectionName, string RowKey);

    private sealed record UpsertWorkItem(
        string CollectionName,
        string RowKey,
        IReadOnlyDictionary<string, string?> Fields) : IngestWorkItem(CollectionName, RowKey);

    private sealed record DeleteWorkItem(string CollectionName, string RowKey) : IngestWorkItem(CollectionName, RowKey);

    private sealed class CollectionIngestQueue
    {
        private readonly string _collectionName;
        private readonly IViewEngine _engine;
        private readonly ILogger _logger;
        private readonly Channel<IngestWorkItem> _channel;
        private readonly CancellationToken _shutdownToken;

        public CollectionIngestQueue(
            string collectionName,
            int capacity,
            IViewEngine engine,
            ILogger logger,
            CancellationToken shutdownToken)
        {
            _collectionName = collectionName;
            _engine = engine;
            _logger = logger;
            _shutdownToken = shutdownToken;
            _channel = Channel.CreateBounded<IngestWorkItem>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
            _ = Task.Run(ProcessAsync);
        }

        public ValueTask EnqueueAsync(IngestWorkItem workItem, CancellationToken ct)
        {
            return _channel.Writer.WriteAsync(workItem, ct);
        }

        private async Task ProcessAsync()
        {
            try
            {
                await foreach (var workItem in _channel.Reader.ReadAllAsync(_shutdownToken).ConfigureAwait(false))
                {
                    await ProcessWorkItemAsync(workItem, _shutdownToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (_shutdownToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "TCP ingest queue for collection '{CollectionName}' stopped unexpectedly.", _collectionName);
            }
        }

        private async Task ProcessWorkItemAsync(IngestWorkItem workItem, CancellationToken ct)
        {
            var result = workItem switch
            {
                UpsertWorkItem upsert => await _engine.IngestAsync(
                        new UpsertRowCommand
                        {
                            CollectionId = upsert.CollectionName,
                            Key = upsert.RowKey,
                            Fields = upsert.Fields
                        },
                        ct)
                    .ConfigureAwait(false),
                DeleteWorkItem delete => await _engine.IngestAsync(
                        new DeleteRowCommand
                        {
                            CollectionId = delete.CollectionName,
                            Key = delete.RowKey
                        },
                        ct)
                    .ConfigureAwait(false),
                _ => throw new InvalidOperationException($"Unsupported ingest work item '{workItem.GetType().Name}'.")
            };

            if (!result.Success)
            {
                _logger.LogWarning(
                    "Queued TCP {Operation} failed for '{CollectionName}' key '{RowKey}': {Error}",
                    workItem is UpsertWorkItem ? "upsert" : "delete",
                    workItem.CollectionName,
                    workItem.RowKey,
                    result.Error ?? "Unknown error");
            }
        }
    }
}
