using System.Threading.Channels;
using System.Threading;

namespace LiveViewEngine.Core;

public interface IWorkItem
{
    ValueTask ExecuteAsync();
}

public interface IWorkItem<T> : IWorkItem
{
    TaskCompletionSource<T> Completion { get; }
}

internal sealed class CollectionWorker : IDisposable
{
    private readonly Channel<IWorkItem> _queue = Channel.CreateUnbounded<IWorkItem>( new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly Lock _startLock = new();
    private int _queuedCount;
    private bool _started;
    private Task? _workerTask;

    public int QueuedCount => Volatile.Read(ref _queuedCount);

    public void Start()
    {
        lock (_startLock)
        {
            if (_started)
            {
                return;
            }

            _started = true;
            _workerTask = Task.Run(ProcessQueueAsync, _cts.Token);
        }
    }

    public async Task<T> EnqueueAsync<T>(IWorkItem<T> work, CancellationToken ct = default)
    {
        await _queue.Writer.WriteAsync(work, ct).ConfigureAwait(false);
        Interlocked.Increment(ref _queuedCount);
        try
        {
            return await work.Completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return await work.Completion.Task.ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _queue.Writer.TryComplete();

        if (_workerTask is not null)
        {
            try
            {
                _workerTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
            catch (AggregateException ex) when (ex.InnerException is ChannelClosedException)
            {
            }
        }

        _cts.Dispose();
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var item in _queue.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                Interlocked.Decrement(ref _queuedCount);
                await item.ExecuteAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }
}
