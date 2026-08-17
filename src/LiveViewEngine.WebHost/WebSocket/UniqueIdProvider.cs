using System.Threading;

namespace ViewEngineServer.WebApp.WebSocket;

internal sealed class UniqueIdProvider
{
    private int _nextId;

    public int Next() => Interlocked.Increment(ref _nextId);
}
