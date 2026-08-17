using System.Diagnostics;

namespace LiveViewEngine.Poc.DataProvider.Services;

public sealed class RateLimiter
{
    private readonly Stopwatch _sw = new();
    private long _nextTick;
    private long _ticksPerSlot;

    public int TargetRatePerSecond { get; private set; }

    public void Configure(int ratePerSecond)
    {
        TargetRatePerSecond = ratePerSecond;
        _ticksPerSlot = ratePerSecond > 0 ? Stopwatch.Frequency / ratePerSecond : 0L;
    }

    public void Start()
    {
        _sw.Restart();
        _nextTick = _sw.ElapsedTicks;
    }

    public void Wait()
    {
        if (_ticksPerSlot == 0)
        {
            return;
        }

        while (_sw.ElapsedTicks < _nextTick)
        {
            Thread.SpinWait(20);
        }

        _nextTick += _ticksPerSlot;
    }
}
