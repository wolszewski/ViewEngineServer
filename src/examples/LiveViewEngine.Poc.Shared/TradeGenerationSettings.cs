namespace LiveViewEngine.Poc.Shared;

public class TradeGenerationSettings
{
    public int InitialTradeCount { get; set; } = 10_000;
    public int UpdateFieldCount { get; set; } = 5;
    public int UpdateFrequencyHz { get; set; } = 10;
    public IReadOnlyList<string>? UpdatableFields { get; set; }
}

public class TradeGenerationStatus
{
    public bool IsRunning { get; set; }
    public bool IsInUpdateMode { get; set; }
    public int InitialTradeCount { get; set; }
    public int UpdateFieldCount { get; set; }
    public int UpdateFrequencyHz { get; set; }
    public int TradesGenerated { get; set; }
    public int UpdatesSent { get; set; }
    public double UpdatesPerSecond { get; set; }
    public string StatusMessage { get; set; } = "Idle";
    public string? LastError { get; set; }
    public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public static TradeGenerationStatus Idle() => new();
}
