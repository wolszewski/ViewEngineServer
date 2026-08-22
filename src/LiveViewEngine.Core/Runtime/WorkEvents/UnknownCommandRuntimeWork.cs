namespace LiveViewEngine.Core.Runtime;

internal sealed class UnknownCommandRuntimeWork : RuntimeWorkItem<MutationResult>
{
    private readonly IngestCommand _command;

    public UnknownCommandRuntimeWork(IngestCommand command)
    {
        _command = command;
    }

    protected override MutationResult ExecuteCore() => new(
        IngestResult.Fail($"Unknown command type '{_command.GetType().Name}'."),
        null);
}