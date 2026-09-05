namespace Vok.Features.Stage;

public enum StageRelinkSelectionStatus
{
    Cancelled,
    Selected,
}

public readonly record struct StageRelinkSelection(
    StageRelinkSelectionStatus Status,
    string? Path)
{
    public static StageRelinkSelection Cancelled =>
        new(StageRelinkSelectionStatus.Cancelled, null);

    public static StageRelinkSelection Selected(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new StageRelinkSelection(StageRelinkSelectionStatus.Selected, path);
    }
}
