namespace Vok.Core.Launcher;

public sealed record LauncherAddResult(
    IReadOnlyList<LauncherItem> Added,
    IReadOnlyList<LauncherItem> Existing);

public sealed record LauncherPage(
    IReadOnlyList<LauncherItem> Items,
    long? NextSortOrder,
    int TotalCount);
