namespace LiveViewEngine.Core.DataIngest;

public sealed class IngestResult
{
    public bool Success { get; private init; }
    public string? Error { get; private init; }

    public static IngestResult Ok() => new() { Success = true };
    public static IngestResult Fail(string error) => new() { Success = false, Error = error };
}