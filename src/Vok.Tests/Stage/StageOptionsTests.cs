using Vok.Core.Stage;
using Xunit;

namespace Vok.Tests.Stage;

public sealed class StageOptionsTests
{
    [Fact]
    public void DefaultCapacityIsFifty()
    {
        var options = new StageOptions();

        Assert.Equal(50, options.Capacity);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    [InlineData(10_000)]
    public void PositiveCapacityIsNotBoundedByTheInitialProductMaximum(long capacity)
    {
        var options = new StageOptions(capacity);

        Assert.Equal(capacity, options.Capacity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveCapacityIsRejected(long capacity)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new StageOptions(capacity));
    }
}
