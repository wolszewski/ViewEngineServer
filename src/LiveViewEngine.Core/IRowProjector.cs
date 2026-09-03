namespace LiveViewEngine.Core;

// Copies selected fields from a full row into the payload sent to a subscriber. The default
// (SelectRowProjector) implements plain column selection; hosts can supply a custom IRowProjector
// (e.g. for computed/derived columns or per-connection field redaction) via
// LiveViewEngineOptions.RowProjector without touching the ingest/sort/filter/delta pipeline.
public interface IRowProjector
{
    string?[] Project(string?[] source, int[] selectedFieldIndexes);
}

public sealed class SelectRowProjector : IRowProjector
{
    public static readonly SelectRowProjector Instance = new();

    public string?[] Project(string?[] source, int[] selectedFieldIndexes)
    {
        var copy = new string?[selectedFieldIndexes.Length];
        for (int i = 0; i < selectedFieldIndexes.Length; i++)
        {
            copy[i] = source[selectedFieldIndexes[i]];
        }

        return copy;
    }
}
