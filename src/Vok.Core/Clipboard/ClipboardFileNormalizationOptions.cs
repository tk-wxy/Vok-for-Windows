namespace Vok.Core.Clipboard;

public sealed class ClipboardFileNormalizationOptions
{
    public const int DefaultMaximumFileCount = 4096;
    public const int DefaultMaximumPathCharacters = 32_767;
    public const int DefaultMaximumAggregatePathCharacters = 1_048_576;

    public ClipboardFileNormalizationOptions(
        int maximumFileCount = DefaultMaximumFileCount,
        int maximumPathCharacters = DefaultMaximumPathCharacters,
        int maximumAggregatePathCharacters = DefaultMaximumAggregatePathCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumFileCount, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maximumPathCharacters, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(
            maximumAggregatePathCharacters,
            maximumPathCharacters);

        MaximumFileCount = maximumFileCount;
        MaximumPathCharacters = maximumPathCharacters;
        MaximumAggregatePathCharacters = maximumAggregatePathCharacters;
    }

    public int MaximumFileCount { get; }

    public int MaximumPathCharacters { get; }

    public int MaximumAggregatePathCharacters { get; }
}
