namespace LiveViewEngine.Core.UnitTests;

public class CollectionWorkerTests
{
    [Fact]
    public async Task EnqueueAsync_CancellationAfterQueueing_StillReturnsCompletion()
    {
        using var worker = new CollectionWorker();
        worker.Start();

        var workStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var work = new TestWorkItem<int>(() =>
        {
            workStarted.TrySetResult();
            allowCompletion.Task.GetAwaiter().GetResult();
            return 7;
        });

        using var cts = new CancellationTokenSource();
        var enqueueTask = worker.EnqueueAsync(work, cts.Token);
        await workStarted.Task;

        cts.Cancel();
        allowCompletion.TrySetResult();

        var result = await enqueueTask;
        Assert.Equal(7, result);
    }

    private sealed class TestWorkItem<T>(Func<T> execute) : IWorkItem<T>
    {
        public TaskCompletionSource<T> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask ExecuteAsync()
        {
            Completion.TrySetResult(execute());
            return ValueTask.CompletedTask;
        }
    }
}
