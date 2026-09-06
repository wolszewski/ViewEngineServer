using LiveViewEngine.Poc.Shared;

namespace Lightstreamer.DataProvider.Services;

public sealed class TradeGenerationSettingsStore
{
    private readonly Lock _sync = new();
    private TradeGenerationSettings _settings = new();

    public TradeGenerationSettings GetSnapshot()
    {
        lock (_sync)
        {
            return Clone(_settings);
        }
    }

    public void Update(TradeGenerationSettings settings)
    {
        lock (_sync)
        {
            _settings = Clone(settings);
        }
    }

    private static TradeGenerationSettings Clone(TradeGenerationSettings settings)
    {
        return new TradeGenerationSettings
        {
            InitialTradeCount = settings.InitialTradeCount,
            UpdateFieldCount = settings.UpdateFieldCount,
            UpdateFrequencyHz = settings.UpdateFrequencyHz,
            OrderedUpdates = settings.OrderedUpdates,
            UpdatableFields = settings.UpdatableFields is null ? null : [.. settings.UpdatableFields]
        };
    }
}
