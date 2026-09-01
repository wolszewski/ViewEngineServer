namespace LiveViewEngine.Core.Runtime.WorkEvents;

internal abstract class RuntimeWorkItem<T> : IWorkItem<T>
{
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<T>? _onCompleted;

    protected RuntimeWorkItem(Action<T>? onCompleted = null)
    {
        _onCompleted = onCompleted;
    }

    public TaskCompletionSource<T> Completion => _completion;

    public void Execute()
    {
        try
        {
            var result = ExecuteCore();
            _onCompleted?.Invoke(result);
            _completion.TrySetResult(result);
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
    }

    protected abstract T ExecuteCore();
}