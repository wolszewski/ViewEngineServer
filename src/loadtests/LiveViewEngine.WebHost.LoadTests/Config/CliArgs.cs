namespace LiveViewEngine.WebHost.LoadTests.Config;

/// <summary>
/// Parses this load test's own CLI flags (distinct from NBomber's built-in --config/--infra-config
/// flags) and strips them out, returning the remaining args untouched for NBomberRunner.Run(...).
/// </summary>
public static class CliArgs
{
    public static string[] ParseAndApply(string[] args, LoadTestSettings settings)
    {
        var remaining = new List<string>(args.Length);

        foreach (var arg in args)
        {
            if (TryGetValue(arg, "--ingest=", out var ingest))
            {
                settings.IngestMode = ingest.Equals("tcp", StringComparison.OrdinalIgnoreCase)
                    ? IngestMode.Tcp
                    : IngestMode.Http;
            }
            else if (TryGetValue(arg, "--scenario=", out var scenario))
            {
                settings.Scenario = scenario.ToLowerInvariant() switch
                {
                    "subscribe" => LoadTestScenario.Subscribe,
                    "delta" => LoadTestScenario.Delta,
                    _ => LoadTestScenario.All
                };
            }
            else if (arg.Equals("--studio", StringComparison.OrdinalIgnoreCase))
            {
                settings.EnableStudio = true;
            }
            else if (TryGetValue(arg, "--collection=", out var collection))
            {
                settings.CollectionName = collection;
            }
            else if (TryGetValue(arg, "--ws-url=", out var wsUrl))
            {
                settings.WebSocketUrl = wsUrl;
            }
            else if (TryGetValue(arg, "--http-url=", out var httpUrl))
            {
                settings.BaseHttpUrl = httpUrl;
            }
            else if (TryGetValue(arg, "--tcp-host=", out var tcpHost))
            {
                settings.TcpHost = tcpHost;
            }
            else if (TryGetValue(arg, "--tcp-port=", out var tcpPort) && int.TryParse(tcpPort, out var port))
            {
                settings.TcpPort = port;
            }
            else if (TryGetValue(arg, "--connections=", out var connections) && int.TryParse(connections, out var count))
            {
                settings.SubscribeCopies = count;
                settings.DeltaSubscriberCount = count;
            }
            else
            {
                remaining.Add(arg);
            }
        }

        return remaining.ToArray();
    }

    private static bool TryGetValue(string arg, string prefix, out string value)
    {
        if (arg.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            value = arg[prefix.Length..];
            return true;
        }

        value = string.Empty;
        return false;
    }
}
