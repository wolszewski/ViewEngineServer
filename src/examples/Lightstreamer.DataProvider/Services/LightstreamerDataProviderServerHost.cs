using System.Net;
using System.Net.Sockets;
using Lightstreamer.DotNet.Server;
using Lightstreamer.Interfaces.Data;

namespace Lightstreamer.DataProvider.Services;

public sealed class LightstreamerDataProviderServerHost<T> 
(
    T adapter,
    string host,
    int port,
    string adapterName,
    ILogger<LightstreamerDataProviderServerHost<T>> logger) : BackgroundService where T:IDataProvider
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            DataProviderServer? server = null;

            try
            {
                var endpoint = new IPEndPoint(IPAddress.Loopback, port);
                logger.LogInformation("Connecting to Lightstreamer adapter port {Host}:{Port} for {AdapterName}", host, port, adapterName);
                client = new TcpClient();
                await client.ConnectAsync(endpoint, stoppingToken);

                await using Stream stream = client.GetStream();
                server = new DataProviderServer
                {
                    Adapter = adapter,
                    AdapterConfig = null,
                    Name = adapterName,
                    RequestStream = stream,
                    ReplyStream = stream,
                    ExceptionHandler = new AdapterExceptionHandler(adapterName, logger)
                };

                server.Start();
                logger.LogInformation("Lightstreamer adapter '{AdapterName}' started. Waiting for cancellation.", adapterName);
                await Task.Delay(Timeout.Infinite, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // OperationCanceledException does not have an 'ex' variable
                logger.LogWarning("Lightstreamer adapter '{AdapterName}' connection canceled. Retrying...", adapterName);

            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "Lightstreamer adapter '{AdapterName}' connection failed. Retrying...", adapterName);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled Lightstreamer adapter error for '{AdapterName}'.", adapterName);
            }
            finally
            {
                try { server?.Close(); } catch { }
                client?.Dispose();
            }

            await Task.Delay(TimeSpan.MaxValue, stoppingToken);
        }
    }

    private sealed class AdapterExceptionHandler(string adapterName, ILogger logger) : IExceptionHandler
    {
        public bool handleException(Exception exception)
        {
            logger.LogError(exception, "Lightstreamer adapter error for {AdapterName}", adapterName);
            return true;
        }

        public bool handleIOException(Exception exception)
        {
            logger.LogError(exception, "Lightstreamer adapter IO error for {AdapterName}", adapterName);
            return true;
        }
    }
}
