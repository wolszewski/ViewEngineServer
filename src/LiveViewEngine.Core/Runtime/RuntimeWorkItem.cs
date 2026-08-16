namespace LiveViewEngine.Core.Runtime;

internal abstract class RuntimeWorkItem<T> : IWorkItem<T>
{
    private readonly TaskCompletionSource<T> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<T> Completion => _completion;

    public void Execute()
    {
        try
        {
            _completion.TrySetResult(ExecuteCore());
        }
        catch (Exception ex)
        {
            _completion.TrySetException(ex);
        }
    }

    protected abstract T ExecuteCore();
}