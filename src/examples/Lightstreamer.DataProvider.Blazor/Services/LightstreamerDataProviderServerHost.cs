using System.Net.Sockets;
using Lightstreamer.DotNet.Server;
using Lightstreamer.Interfaces.Data;

namespace Lightstreamer.DataProvider.Blazor.Services;

public sealed class LightstreamerDataProviderServerHost(
    TradeDataProvider adapter,
    ILogger<LightstreamerDataProviderServerHost> logger,
    IConfiguration configuration) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var host = configuration["Lightstreamer__Host"] ?? "127.0.0.1";
        var port = configuration.GetValue<int>("Lightstreamer__RequestReplyPort", 6661);
        var adapterName = configuration["Lightstreamer__AdapterName"] ?? "TRADES_ADAPTER";

        while (!stoppingToken.IsCancellationRequested)
        {
            TcpClient? client = null;
            DataProviderServer? server = null;

            try
            {
                logger.LogInformation("Connecting to Lightstreamer adapter port {Host}:{Port} for {AdapterName}", host, port, adapterName);
                client = new TcpClient();
                await client.ConnectAsync(host, port, stoppingToken);

                var stream = client.GetStream();
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
                break;
            }
            catch (SocketException ex)
            {
                logger.LogWarning(ex, "Lightstreamer adapter connection failed. Retrying...");
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Unhandled Lightstreamer adapter error.");
            }
            finally
            {
                try
                {
                    server?.Close();
                }
                catch
                {
                    // Ignore shutdown races.
                }

                client?.Dispose();
            }

            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
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
