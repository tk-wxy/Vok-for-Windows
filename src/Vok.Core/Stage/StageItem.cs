namespace Vok.Core.Stage;

public sealed record StageItem(
    Guid Id,
    StageItemKind Kind,
    string TargetValue,
    string CanonicalKey,
    string ResolvedName,
    long SortOrder,
    bool IsPinned,
    long Revision,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
