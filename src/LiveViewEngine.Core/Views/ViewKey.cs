namespace LiveViewEngine.Core.Views;

public sealed class ViewKey : IEquatable<ViewKey>
{
    public string CollectionId { get; }
    public string? SortColumn { get; }
    public bool SortAscending { get; }
    public IReadOnlyList<FilterSpec> Filters { get; }

    public string Id { get; }

    private readonly int _hashCode;

    public ViewKey(string collectionId, string? sortColumn, bool sortAscending,
        IReadOnlyList<FilterSpec>? filters)
    {
        CollectionId = collectionId;
        SortColumn = sortColumn;
        SortAscending = sortAscending;
        Filters = filters ?? [];

        Id = $"{collectionId}|{sortColumn}|{(sortAscending ? "asc" : "desc")}|" +
             string.Join(",", Filters.Select(f => $"{f.FieldName}:{f.Operator}:{f.Value}"));

        _hashCode = ComputeHash();
    }

    public static ViewKey From(ViewDefinition def) =>
        new(def.CollectionId, def.SortColumn, def.SortAscending, def.Filters);

    public bool Equals(ViewKey? other)
    {
        if (other is null)
        {
            return false;
        }

        if (CollectionId != other.CollectionId)
        {
            return false;
        }

        if (SortColumn != other.SortColumn)
        {
            return false;
        }

        if (SortAscending != other.SortAscending)
        {
            return false;
        }

        if (Filters.Count != other.Filters.Count)
        {
            return false;
        }

        for (int i = 0; i < Filters.Count; i++)
        {
            var f = Filters[i]; var o = other.Filters[i];
            if (f.FieldName != o.FieldName ||
                f.Operator != o.Operator ||
                !Equals(f.Value, o.Value))
            {
                return false;
            }
        }
        return true;
    }

    public override bool Equals(object? obj) => obj is ViewKey vk && Equals(vk);
    public override int GetHashCode() => _hashCode;
    public override string ToString() => Id;

    private int ComputeHash()
    {
        var hc = new HashCode();
        hc.Add(CollectionId);
        hc.Add(SortColumn);
        hc.Add(SortAscending);
        foreach (var f in Filters)
        {
            hc.Add(f.FieldName);
            hc.Add(f.Operator);
            hc.Add(f.Value);
        }
        return hc.ToHashCode();
    }
}