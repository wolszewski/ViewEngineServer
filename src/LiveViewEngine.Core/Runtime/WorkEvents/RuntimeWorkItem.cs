namespace LiveViewEngine.Core.Runtime.WorkEvents;

internal abstract class RuntimeWorkItem<T> : IWorkItem<T>
{
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Func<T, ValueTask>? _onCompleted;

    protected RuntimeWorkItem(Func<T, ValueTask>? onCompleted = null)
    {
        _onCompleted = onCompleted;
    }

    public TaskCompletionSource<T> Completion => _completion;

    public async ValueTask ExecuteAsync()
    {
        try
        {
            var result = ExecuteCore();
            if (_onCompleted is not null)
            {
                await _onCompleted(result).ConfigureAwait(false);
            }

            _completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
    }

    protected abstract T ExecuteCore();
}