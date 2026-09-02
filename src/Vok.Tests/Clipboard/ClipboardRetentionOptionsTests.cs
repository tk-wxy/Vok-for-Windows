using Vok.Core.Clipboard;
using Xunit;

namespace Vok.Tests.Clipboard;

public sealed class ClipboardRetentionOptionsTests
{
    [Fact]
    public void DefaultsAreNamedAndBounded()
    {
        var options = new ClipboardRetentionOptions();

        Assert.Equal(100, options.MaximumUnpinnedEntryCount);
        Assert.Equal(TimeSpan.FromDays(30), options.MaximumAge);
        Assert.Equal(512L * 1024 * 1024, options.MaximumTotalBytes);
    }

    [Fact]
    public void InvalidLimitsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClipboardRetentionOptions(maximumUnpinnedEntryCount: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClipboardRetentionOptions(
                maximumUnpinnedEntryCount:
                    ClipboardRetentionOptions.MaximumSupportedEntryCount + 1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClipboardRetentionOptions(maximumAge: TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClipboardRetentionOptions(
                maximumAge: ClipboardRetentionOptions.MaximumSupportedAge +
                    TimeSpan.FromDays(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ClipboardRetentionOptions(maximumTotalBytes: 0));
    }
}
