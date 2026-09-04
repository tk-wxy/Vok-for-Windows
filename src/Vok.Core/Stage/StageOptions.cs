namespace Vok.Core.Stage;

public sealed class StageOptions
{
    public const long DefaultCapacity = 50;

    public StageOptions(long capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        Capacity = capacity;
    }

    public long Capacity { get; }
}
