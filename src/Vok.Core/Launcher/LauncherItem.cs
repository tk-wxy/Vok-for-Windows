namespace Vok.Core.Launcher;

public sealed record LauncherItem(
    Guid Id,
    LauncherItemKind Kind,
    string TargetValue,
    string CanonicalKey,
    string ResolvedName,
    string? CustomName,
    long SortOrder,
    long Revision,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc)
{
    public string DisplayName => string.IsNullOrWhiteSpace(CustomName)
        ? ResolvedName
        : CustomName;

    public bool CanRevealInExplorer => Kind is not LauncherItemKind.ShellNamespace;
}
