using System.Threading.Channels;

namespace LiveViewEngine.Core;

internal sealed class CollectionWorker : IDisposable
{
    private readonly Channel<IWorkItem> _queue = Channel.CreateUnbounded<IWorkItem>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });
    private readonly CancellationTokenSource _cts = new();
    private readonly object _startLock = new();
    private bool _started;
    private Task? _workerTask;

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

    public async Task<T> EnqueueAsync<T>(Func<T> work, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        await _queue.Writer.WriteAsync(new WorkItem<T>(work, completion), ct).ConfigureAwait(false);
        return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
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
                item.Execute();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ChannelClosedException)
        {
        }
    }

    private interface IWorkItem
    {
        void Execute();
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly Func<T> _work;
        private readonly TaskCompletionSource<T> _completion;

        public WorkItem(Func<T> work, TaskCompletionSource<T> completion)
        {
            _work = work;
            _completion = completion;
        }

        public void Execute()
        {
            try
            {
                _completion.TrySetResult(_work());
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
        }
    }
}
