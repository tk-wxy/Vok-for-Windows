namespace Vok.Core.Stage;

public readonly record struct StageItemVersion
{
    public StageItemVersion(Guid id, long revision)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);

        Id = id;
        Revision = revision;
    }

    public Guid Id { get; }

    public long Revision { get; }
}

public readonly record struct StageOrderAnchor : IComparable<StageOrderAnchor>
{
    public StageOrderAnchor(Guid id, long revision, long sortOrder)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(id, Guid.Empty);
        ArgumentOutOfRangeException.ThrowIfLessThan(revision, 1);

        Id = id;
        Revision = revision;
        SortOrder = sortOrder;
    }

    public Guid Id { get; }

    public long Revision { get; }

    public long SortOrder { get; }

    public int CompareTo(StageOrderAnchor other)
    {
        var sortOrderComparison = SortOrder.CompareTo(other.SortOrder);
        return sortOrderComparison != 0
            ? sortOrderComparison
            : StringComparer.Ordinal.Compare(FormatId(Id), FormatId(other.Id));
    }

    internal static string FormatId(Guid id) => id.ToString("D");
}

public sealed record StageReorderRequest
{
    public StageReorderRequest(
        StageItemVersion source,
        StageOrderAnchor? newerNeighbor,
        StageOrderAnchor? olderNeighbor)
    {
        ValidateSource(source);
        if (newerNeighbor is { } validatedNewer)
        {
            ValidateAnchor(validatedNewer, nameof(newerNeighbor));
        }

        if (olderNeighbor is { } validatedOlder)
        {
            ValidateAnchor(validatedOlder, nameof(olderNeighbor));
        }

        if (newerNeighbor?.Id == source.Id || olderNeighbor?.Id == source.Id)
        {
            throw new ArgumentException(
                "The Stage reorder source cannot also be an order anchor.");
        }

        if (newerNeighbor is { } newer && olderNeighbor is { } older)
        {
            if (newer.Id == older.Id)
            {
                throw new ArgumentException(
                    "The Stage reorder anchors must identify different items.");
            }

            if (newer.CompareTo(older) <= 0)
            {
                throw new ArgumentException(
                    "The newer Stage reorder anchor must precede the older anchor.");
            }
        }

        Source = source;
        NewerNeighbor = newerNeighbor;
        OlderNeighbor = olderNeighbor;
    }

    public StageItemVersion Source { get; }

    public StageOrderAnchor? NewerNeighbor { get; }

    public StageOrderAnchor? OlderNeighbor { get; }

    private static void ValidateSource(StageItemVersion source)
    {
        if (source.Id == Guid.Empty || source.Revision < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(source),
                source,
                "The Stage reorder source must have a non-empty id and a positive revision.");
        }
    }

    private static void ValidateAnchor(StageOrderAnchor anchor, string parameterName)
    {
        if (anchor.Id == Guid.Empty || anchor.Revision < 1)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                anchor,
                "A Stage reorder anchor must have a non-empty id and a positive revision.");
        }
    }
}
