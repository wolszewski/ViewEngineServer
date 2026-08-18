using LiveViewEngine.Poc.Shared;

namespace LiveViewEngine.Poc.DataProvider.Services;

public sealed class TradeGenerationSettings : LiveViewEngine.Poc.Shared.TradeGenerationSettings
{
}

public sealed class TradeGenerationStatus : LiveViewEngine.Poc.Shared.TradeGenerationStatus
{
    public new static TradeGenerationStatus Idle() => new();
}

