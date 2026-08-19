using System.Buffers.Text;
using System.Globalization;
using System.IO.Pipelines;
using System.Text;

namespace LiveViewEngine.TcpProtocol;

public static class TcpProtocolCodec
{
    public const int ProtocolVersion = 1;
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public static string SerializeRequest(TcpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return Serialize(writer => WriteRequest(writer, request));
    }

    public static string SerializeResponse(TcpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        return Serialize(writer => WriteResponse(writer, response));
    }

    public static void WriteRequest(PipeWriter writer, TcpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(request);

        switch (request)
        {
            case CreateCollectionRequestMessage create:
                WriteCreateRequest(writer, create);
                break;
            case GetSchemaRequestMessage getSchema:
                WriteCommandWithRequestId(writer, "GET_SCHEMA"u8, getSchema.RequestId);
                WriteSeparator(writer);
                WriteEncodedToken(writer, getSchema.CollectionName);
                break;
            case UpsertRequestMessage upsert:
                WriteUpsertRequest(writer, upsert);
                break;
            case DeleteRequestMessage delete:
                WriteCommandWithRequestId(writer, "DELETE"u8, delete.RequestId);
                WriteSeparator(writer);
                WriteEncodedToken(writer, delete.CollectionName);
                WriteSeparator(writer);
                WriteEncodedToken(writer, delete.RowKey);
                break;
            case PingRequestMessage ping:
                WriteCommandWithRequestId(writer, "PING"u8, ping.RequestId);
                break;
            default:
                throw new InvalidOperationException($"Unsupported request type '{request.GetType().Name}'.");
        }
    }

    public static void WriteRequestLine(PipeWriter writer, TcpRequestMessage request)
    {
        WriteRequest(writer, request);
        WriteNewLine(writer);
    }

    public static void WriteResponse(PipeWriter writer, TcpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(response);

        switch (response)
        {
            case ConnectedResponseMessage connected:
                WriteCommand(writer, "CONNECTED"u8);
                WriteSeparator(writer);
                WriteInt32(writer, connected.ProtocolVersion);
                break;
            case AckResponseMessage ack:
                WriteCommandWithRequestId(writer, "ACK"u8, ack.RequestId);
                WriteSeparator(writer);
                WriteEncodedToken(writer, ack.Operation);
                break;
            case ErrorResponseMessage error:
                WriteCommandWithRequestId(writer, "ERR"u8, error.RequestId);
                WriteSeparator(writer);
                WriteEncodedToken(writer, error.Message);
                break;
            case SchemaResponseMessage schema:
                WriteSchemaResponse(writer, schema);
                break;
            case PongResponseMessage pong:
                WriteCommandWithRequestId(writer, "PONG"u8, pong.RequestId);
                break;
            default:
                throw new InvalidOperationException($"Unsupported response type '{response.GetType().Name}'.");
        }
    }

    public static void WriteResponseLine(PipeWriter writer, TcpResponseMessage response)
    {
        WriteResponse(writer, response);
        WriteNewLine(writer);
    }

    public static TcpRequestMessage ParseRequest(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        var tokens = line.Split('|');
        if (tokens.Length < 2)
        {
            throw new FormatException("Request must contain a command and request id.");
        }

        return tokens[0] switch
        {
            "CREATE" => ParseCreateRequest(tokens),
            "GET_SCHEMA" => ParseGetSchemaRequest(tokens),
            "UPSERT" => ParseUpsertRequest(tokens),
            "DELETE" => ParseDeleteRequest(tokens),
            "PING" => ParsePingRequest(tokens),
            _ => throw new FormatException($"Unknown request command '{tokens[0]}'.")
        };
    }

    public static TcpResponseMessage ParseResponse(string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(line);

        var tokens = line.Split('|');
        if (tokens.Length == 0)
        {
            throw new FormatException("Response is empty.");
        }

        return tokens[0] switch
        {
            "CONNECTED" => ParseConnectedResponse(tokens),
            "ACK" => ParseAckResponse(tokens),
            "ERR" => ParseErrorResponse(tokens),
            "SCHEMA" => ParseSchemaResponse(tokens),
            "PONG" => ParsePongResponse(tokens),
            _ => throw new FormatException($"Unknown response command '{tokens[0]}'.")
        };
    }

    public static string EncodeToken(string? value)
    {
        return value switch
        {
            null => "#",
            "" => "$",
            _ => Uri.EscapeDataString(value)
        };
    }

    public static string? DecodeToken(string token)
    {
        return token switch
        {
            "#" => null,
            "$" => string.Empty,
            _ => Uri.UnescapeDataString(token)
        };
    }

    private static TcpRequestMessage ParseCreateRequest(string[] tokens)
    {
        if (tokens.Length < 4)
        {
            throw new FormatException("CREATE requires collection name and field count.");
        }

        var requestId = ParseInt64(tokens[1], "request id");
        var collectionName = DecodeRequired(tokens[2], "collection name");
        var fieldCount = ParseInt32(tokens[3], "field count");
        var expectedLength = 4 + fieldCount * 2;
        if (tokens.Length != expectedLength)
        {
            throw new FormatException($"CREATE expected {expectedLength} tokens but received {tokens.Length}.");
        }

        var fields = new List<TcpSchemaField>(fieldCount);
        for (var index = 0; index < fieldCount; index++)
        {
            var name = DecodeRequired(tokens[4 + index * 2], $"field name {index}");
            var type = DecodeRequired(tokens[5 + index * 2], $"field type {index}");
            fields.Add(new TcpSchemaField(index + 1, name, type));
        }

        return new CreateCollectionRequestMessage(requestId, collectionName, fields);
    }

    private static TcpRequestMessage ParseGetSchemaRequest(string[] tokens)
    {
        if (tokens.Length != 3)
        {
            throw new FormatException("GET_SCHEMA requires exactly three tokens.");
        }

        return new GetSchemaRequestMessage(
            ParseInt64(tokens[1], "request id"),
            DecodeRequired(tokens[2], "collection name"));
    }

    private static TcpRequestMessage ParseUpsertRequest(string[] tokens)
    {
        if (tokens.Length < 5)
        {
            throw new FormatException("UPSERT requires collection, key, and field count.");
        }

        var requestId = ParseInt64(tokens[1], "request id");
        var collectionName = DecodeRequired(tokens[2], "collection name");
        var rowKey = DecodeRequired(tokens[3], "row key");
        var pairCount = ParseInt32(tokens[4], "pair count");
        var expectedLength = 5 + pairCount * 2;
        if (tokens.Length != expectedLength)
        {
            throw new FormatException($"UPSERT expected {expectedLength} tokens but received {tokens.Length}.");
        }

        var fields = new List<KeyValuePair<int, string?>>(pairCount);
        for (var index = 0; index < pairCount; index++)
        {
            var fieldIndex = ParseInt32(tokens[5 + index * 2], $"field index {index}");
            var value = DecodeToken(tokens[6 + index * 2]);
            fields.Add(new KeyValuePair<int, string?>(fieldIndex, value));
        }

        return new UpsertRequestMessage(requestId, collectionName, rowKey, fields);
    }

    private static TcpRequestMessage ParseDeleteRequest(string[] tokens)
    {
        if (tokens.Length != 4)
        {
            throw new FormatException("DELETE requires exactly four tokens.");
        }

        return new DeleteRequestMessage(
            ParseInt64(tokens[1], "request id"),
            DecodeRequired(tokens[2], "collection name"),
            DecodeRequired(tokens[3], "row key"));
    }

    private static TcpRequestMessage ParsePingRequest(string[] tokens)
    {
        if (tokens.Length != 2)
        {
            throw new FormatException("PING requires exactly two tokens.");
        }

        return new PingRequestMessage(ParseInt64(tokens[1], "request id"));
    }

    private static TcpResponseMessage ParseConnectedResponse(string[] tokens)
    {
        if (tokens.Length != 2)
        {
            throw new FormatException("CONNECTED requires exactly two tokens.");
        }

        return new ConnectedResponseMessage(ParseInt32(tokens[1], "protocol version"));
    }

    private static TcpResponseMessage ParseAckResponse(string[] tokens)
    {
        if (tokens.Length != 3)
        {
            throw new FormatException("ACK requires exactly three tokens.");
        }

        return new AckResponseMessage(
            ParseInt64(tokens[1], "request id"),
            DecodeRequired(tokens[2], "operation"));
    }

    private static TcpResponseMessage ParseErrorResponse(string[] tokens)
    {
        if (tokens.Length != 3)
        {
            throw new FormatException("ERR requires exactly three tokens.");
        }

        return new ErrorResponseMessage(
            ParseInt64(tokens[1], "request id"),
            DecodeRequired(tokens[2], "message"));
    }

    private static TcpResponseMessage ParseSchemaResponse(string[] tokens)
    {
        if (tokens.Length < 4)
        {
            throw new FormatException("SCHEMA requires request id, collection, and field count.");
        }

        var requestId = ParseInt64(tokens[1], "request id");
        var collectionName = DecodeRequired(tokens[2], "collection name");
        var fieldCount = ParseInt32(tokens[3], "field count");
        var expectedLength = 4 + fieldCount * 3;
        if (tokens.Length != expectedLength)
        {
            throw new FormatException($"SCHEMA expected {expectedLength} tokens but received {tokens.Length}.");
        }

        var fields = new List<TcpSchemaField>(fieldCount);
        for (var index = 0; index < fieldCount; index++)
        {
            var fieldIndex = ParseInt32(tokens[4 + index * 3], $"schema index {index}");
            var name = DecodeRequired(tokens[5 + index * 3], $"schema field name {index}");
            var type = DecodeRequired(tokens[6 + index * 3], $"schema field type {index}");
            fields.Add(new TcpSchemaField(fieldIndex, name, type));
        }

        return new SchemaResponseMessage(requestId, collectionName, fields);
    }

    private static TcpResponseMessage ParsePongResponse(string[] tokens)
    {
        if (tokens.Length != 2)
        {
            throw new FormatException("PONG requires exactly two tokens.");
        }

        return new PongResponseMessage(ParseInt64(tokens[1], "request id"));
    }

    private static string Serialize(Action<PipeWriter> write)
    {
        using var stream = new MemoryStream();
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        write(writer);
        writer.FlushAsync().GetAwaiter().GetResult();
        writer.CompleteAsync().GetAwaiter().GetResult();
        return Utf8.GetString(stream.GetBuffer(), 0, checked((int)stream.Length));
    }

    private static void WriteCreateRequest(PipeWriter writer, CreateCollectionRequestMessage request)
    {
        WriteCommandWithRequestId(writer, "CREATE"u8, request.RequestId);
        WriteSeparator(writer);
        WriteEncodedToken(writer, request.CollectionName);
        WriteSeparator(writer);
        WriteInt32(writer, request.Fields.Count);
        for (var index = 0; index < request.Fields.Count; index++)
        {
            WriteSeparator(writer);
            WriteEncodedToken(writer, request.Fields[index].Name);
            WriteSeparator(writer);
            WriteEncodedToken(writer, request.Fields[index].Type);
        }
    }

    private static void WriteUpsertRequest(PipeWriter writer, UpsertRequestMessage request)
    {
        WriteCommandWithRequestId(writer, "UPSERT"u8, request.RequestId);
        WriteSeparator(writer);
        WriteEncodedToken(writer, request.CollectionName);
        WriteSeparator(writer);
        WriteEncodedToken(writer, request.RowKey);
        WriteSeparator(writer);
        WriteInt32(writer, request.Fields.Count);
        for (var index = 0; index < request.Fields.Count; index++)
        {
            WriteSeparator(writer);
            WriteInt32(writer, request.Fields[index].Key);
            WriteSeparator(writer);
            WriteEncodedToken(writer, request.Fields[index].Value);
        }
    }

    private static void WriteSchemaResponse(PipeWriter writer, SchemaResponseMessage response)
    {
        WriteCommandWithRequestId(writer, "SCHEMA"u8, response.RequestId);
        WriteSeparator(writer);
        WriteEncodedToken(writer, response.CollectionName);
        WriteSeparator(writer);
        WriteInt32(writer, response.Fields.Count);
        for (var index = 0; index < response.Fields.Count; index++)
        {
            var field = response.Fields[index];
            WriteSeparator(writer);
            WriteInt32(writer, field.Index);
            WriteSeparator(writer);
            WriteEncodedToken(writer, field.Name);
            WriteSeparator(writer);
            WriteEncodedToken(writer, field.Type);
        }
    }

    private static void WriteCommand(PipeWriter writer, ReadOnlySpan<byte> command)
    {
        var span = writer.GetSpan(command.Length);
        command.CopyTo(span);
        writer.Advance(command.Length);
    }

    private static void WriteCommandWithRequestId(PipeWriter writer, ReadOnlySpan<byte> command, long requestId)
    {
        WriteCommand(writer, command);
        WriteSeparator(writer);
        WriteInt64(writer, requestId);
    }

    private static void WriteEncodedToken(PipeWriter writer, string? value)
    {
        WriteString(writer, EncodeToken(value));
    }

    private static void WriteString(PipeWriter writer, string value)
    {
        var maxByteCount = Utf8.GetMaxByteCount(value.Length);
        var span = writer.GetSpan(maxByteCount);
        var written = Utf8.GetBytes(value, span);
        writer.Advance(written);
    }

    private static void WriteSeparator(PipeWriter writer)
    {
        var span = writer.GetSpan(1);
        span[0] = (byte)'|';
        writer.Advance(1);
    }

    private static void WriteNewLine(PipeWriter writer)
    {
        var span = writer.GetSpan(1);
        span[0] = (byte)'\n';
        writer.Advance(1);
    }

    private static void WriteInt32(PipeWriter writer, int value)
    {
        var span = writer.GetSpan(11);
        if (!Utf8Formatter.TryFormat(value, span, out var written))
        {
            throw new InvalidOperationException("Could not serialize Int32 value.");
        }

        writer.Advance(written);
    }

    private static void WriteInt64(PipeWriter writer, long value)
    {
        var span = writer.GetSpan(20);
        if (!Utf8Formatter.TryFormat(value, span, out var written))
        {
            throw new InvalidOperationException("Could not serialize Int64 value.");
        }

        writer.Advance(written);
    }

    private static string DecodeRequired(string token, string name)
    {
        return DecodeToken(token)
            ?? throw new FormatException($"The {name} token cannot be null.");
    }

    private static int ParseInt32(string token, string name)
    {
        if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"The {name} token '{token}' is not a valid integer.");
        }

        return value;
    }

    private static long ParseInt64(string token, string name)
    {
        if (!long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
        {
            throw new FormatException($"The {name} token '{token}' is not a valid integer.");
        }

        return value;
    }
}
