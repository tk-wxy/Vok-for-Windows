using Vok.Core.Clipboard;
using Xunit;

namespace Vok.Tests.Clipboard;

public sealed class ClipboardFileSemanticIdentityTests
{
    [Fact]
    public void LengthPrefixPreventsPathBoundaryAmbiguity()
    {
        var first = ClipboardFileSemanticIdentity.Compute(
            ClipboardFileTransferIntent.Copy,
            [File(0, @"C:\ab"), File(1, @"C:\c")]);
        var second = ClipboardFileSemanticIdentity.Compute(
            ClipboardFileTransferIntent.Copy,
            [File(0, @"C:\a"), File(1, @"C:\bc")]);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void IntentParticipatesInIdentity()
    {
        var file = new[] { File(0, @"C:\A.txt") };

        Assert.NotEqual(
            ClipboardFileSemanticIdentity.Compute(ClipboardFileTransferIntent.Copy, file),
            ClipboardFileSemanticIdentity.Compute(ClipboardFileTransferIntent.Move, file));
    }

    [Fact]
    public void NonContiguousOrdinalsAreRejected()
    {
        Assert.Throws<ArgumentException>(() =>
            ClipboardFileSemanticIdentity.Compute(
                ClipboardFileTransferIntent.Copy,
                [File(1, @"C:\A.txt")]));
    }

    private static ClipboardFileReference File(int ordinal, string path) =>
        new(ordinal, path, path, System.IO.Path.GetFileName(path));
}
