using System.Buffers;
using System.IO.Pipelines;
using System.Net.Sockets;
using System.Text;
using LiveViewEngine.TcpProtocol;
using Microsoft.Extensions.Logging;

namespace ViewEngineServer.WebApp.Tcp;

public sealed class TcpIngestConnectionHandler(
    TcpIngestRequestDispatcher dispatcher,
    ILogger<TcpIngestConnectionHandler> logger)
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false);

    public async Task HandleAsync(TcpClient client, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(client);

        await using var stream = client.GetStream();
        var writer = PipeWriter.Create(stream, new StreamPipeWriterOptions(leaveOpen: true));
        await WriteResponseAsync(writer, new ConnectedResponseMessage(TcpProtocolCodec.ProtocolVersion), ct)
            .ConfigureAwait(false);

        var reader = PipeReader.Create(stream, new StreamPipeReaderOptions(leaveOpen: true));
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var result = await reader.ReadAsync(ct).ConfigureAwait(false);
                var buffer = result.Buffer;

                while (TryReadLine(ref buffer, out var line))
                {
                    await ProcessLineAsync(line, writer, ct).ConfigureAwait(false);
                }

                reader.AdvanceTo(buffer.Start, buffer.End);

                if (result.IsCompleted)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        finally
        {
            await reader.CompleteAsync().ConfigureAwait(false);
            await writer.CompleteAsync().ConfigureAwait(false);
        }
    }

    private async Task ProcessLineAsync(ReadOnlySequence<byte> line, PipeWriter writer, CancellationToken ct)
    {
        var lineText = DecodeLine(line);
        TcpResponseMessage? response;

        try
        {
            var request = TcpProtocolCodec.ParseRequest(lineText);
            response = await dispatcher.DispatchAsync(request, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Invalid TCP ingest request line: {Line}", lineText);
            response = new ErrorResponseMessage(0, ex.Message);
        }

        if (response is not null)
        {
            await WriteResponseAsync(writer, response, ct).ConfigureAwait(false);
        }
    }

    private static async Task WriteResponseAsync(PipeWriter writer, TcpResponseMessage response, CancellationToken ct)
    {
        TcpProtocolCodec.WriteResponseLine(writer, response);
        await writer.FlushAsync(ct).ConfigureAwait(false);
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

        var buffer = ArrayPool<byte>.Shared.Rent((int)line.Length);
        try
        {
            line.CopyTo(buffer);
            return Utf8.GetString(buffer, 0, (int)line.Length);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
