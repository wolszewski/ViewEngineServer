using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ViewEngineServer.WebApp.Tcp;

public sealed class TcpIngestListenerService(
    TcpIngestOptions options,
    TcpIngestConnectionHandler connectionHandler,
    ILogger<TcpIngestListenerService> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, Task> _clientTasks = new();
    private TcpListener? _listener;
    private int _clientId;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("TCP ingest listener is disabled.");
            return;
        }

        _listener = new TcpListener(ParseAddress(options.ListenAddress), options.Port);
        _listener.Start(options.Backlog);
        logger.LogInformation(
            "TCP ingest listener started on {ListenAddress}:{Port}.",
            options.ListenAddress,
            options.Port);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken).ConfigureAwait(false);
                client.NoDelay = true;
                var clientKey = Interlocked.Increment(ref _clientId);
                var task = HandleClientAsync(clientKey, client, stoppingToken);
                _clientTasks[clientKey] = task;
                if (task.IsCompleted)
                {
                    _clientTasks.TryRemove(clientKey, out _);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            _listener.Stop();
            await Task.WhenAll(_clientTasks.Values).ConfigureAwait(false);
            logger.LogInformation("TCP ingest listener stopped.");
        }
    }

    public override Task StopAsync(CancellationToken cancellationToken)
    {
        return base.StopAsync(cancellationToken);
    }

    private async Task HandleClientAsync(int clientKey, TcpClient client, CancellationToken stoppingToken)
    {
        using (client)
        {
            var endpoint = client.Client.RemoteEndPoint?.ToString() ?? $"client-{clientKey}";
            logger.LogInformation("TCP ingest client connected: {Endpoint}", endpoint);

            try
            {
                await connectionHandler.HandleAsync(client, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
            }
            catch (IOException ex)
            {
                logger.LogInformation(ex, "TCP ingest client disconnected: {Endpoint}", endpoint);
            }
            catch (SocketException ex)
            {
                logger.LogInformation(ex, "TCP ingest socket error for {Endpoint}", endpoint);
            }
            finally
            {
                _clientTasks.TryRemove(clientKey, out _);
            }
        }
    }

    private static IPAddress ParseAddress(string address)
    {
        if (string.Equals(address, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        if (!IPAddress.TryParse(address, out var parsed))
        {
            throw new InvalidOperationException($"TCP ingest listen address '{address}' is invalid.");
        }

        return parsed;
    }
}
