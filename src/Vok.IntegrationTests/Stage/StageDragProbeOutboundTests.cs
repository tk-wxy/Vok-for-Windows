using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Windows;
using Vok.IntegrationTests.Infrastructure;
using Vok.StageDragProbe;
using Xunit;
using ComDataObject = System.Runtime.InteropServices.ComTypes.IDataObject;

namespace Vok.IntegrationTests.Stage;

[Collection(WpfApplicationCollection.Name)]
public sealed partial class StageDragProbeOutboundTests(WpfApplicationFixture wpf)
{
    [Theory]
    [InlineData("Single", 1)]
    [InlineData("Batch10", 10)]
    [InlineData("Batch100", 100)]
    [InlineData("Mixed", 4)]
    [InlineData("Duplicate", 6)]
    public void AllProfilesExposeNativeHdropAndRawCopyBytesWithoutLosingOccurrences(string profile, int count)
    {
        wpf.Invoke(() =>
        {
            using var layout = ProbeOutboundTestLayout.Create(Enum.Parse<ProbeFixtureProfile>(profile));
            using var payload = layout.CreateData();
            Assert.Equal(count, payload.Paths.Count);
            Assert.Equal(layout.Bundle.OrderedPaths, payload.Paths);
            Assert.Equal(layout.Bundle.Manifest.RunId, payload.FixtureRunId);
            Assert.Equal(DragDropEffects.Copy, payload.AllowedEffects);
            Assert.Equal(new[] { DataFormats.FileDrop, ProbeOutboundDataObject.PreferredDropEffectFormat,
                    payload.TokenFormat }.Order(), payload.Data.GetFormats(false).Order());
            Assert.Throws<NotSupportedException>(() => ((IList<string>)payload.Paths)[0] = "changed");

            // Exercise WPF's actual COM interface, not just managed GetData returning our own objects.
            for (var round = 0; round < 2; round++)
            {
                Assert.Equal(payload.Paths, ReadNativePaths(payload.Data));
                Assert.Equal(new byte[] { 1, 0, 0, 0 }, ReadNativeBytes(payload.Data,
                    ProbeOutboundDataObject.PreferredDropEffectFormat));
                Assert.Equal(new byte[] { 1 }, ReadNativeBytes(payload.Data, payload.TokenFormat));
            }

            var data = new ProbeOutboundTestData(payload.Data);
            var recorder = new ProbeOutboundTestRecorder();
            using var receiver = new ProbeFileDropReceiver(ProbeReceiverEffect.Copy, "test", recorder);
            Assert.Equal(DragDropEffects.Copy, receiver.Enter(data, payload.AllowedEffects));
            for (var index = 0; index < 100; index++) receiver.Over(payload.AllowedEffects);
            Assert.DoesNotContain(data.Calls, call => call.Operation == "get");
            Assert.Equal(DragDropEffects.Copy, receiver.Drop(data, payload.AllowedEffects));
            var report = Assert.Single(recorder.Reports);
            Assert.Equal(payload.Paths, report.Paths);
            Assert.Equal(new byte[] { 1, 0, 0, 0 }, report.PreferredDropEffectBytes);
            Assert.Equal(1, report.PreferredDropEffectReadCount);
            Assert.Equal(1, report.FileDropReadCount);
            Assert.Equal(1, report.RawEffect);
            Assert.Equal(new[] { ProbeOutboundDataObject.PreferredDropEffectFormat, DataFormats.FileDrop },
                data.Calls.Where(call => call.Operation == "get").Select(call => call.Format));
            var reloaded = ProbeFixtureManifestStore.LoadAndValidate(layout.Output,
                layout.Options.Profile!.Value, layout.Options.SourceRevision);
            Assert.Equal(payload.SourceTreeDigest, reloaded.Manifest.SourceTreeDigest);
            Assert.Equal(payload.OrderedOccurrenceDigest, reloaded.Manifest.OrderedOccurrenceDigest);
        });
    }

    [Fact]
    public void TokenIsPerInstanceNotPerDragAndOwnerDisposesStreamsExactlyOnce()
    {
        wpf.Invoke(() =>
        {
            using var layout = ProbeOutboundTestLayout.Create();
            using var first = layout.CreateData();
            using var next = layout.CreateData();
            using var otherInstance = layout.CreateData(new ProbeOutboundDragToken());
            Assert.Equal(ProbeOutboundDragToken.Current.FormatName, first.TokenFormat);
            Assert.Equal(first.TokenFormat, next.TokenFormat);
            Assert.NotEqual(first.TokenFormat, otherInstance.TokenFormat);
            var effect = Assert.IsType<MemoryStream>(first.Data.GetData(
                ProbeOutboundDataObject.PreferredDropEffectFormat, false));
            var marker = Assert.IsType<MemoryStream>(first.Data.GetData(first.TokenFormat, false));
            first.Dispose();
            first.Dispose();
            Assert.True(first.IsDisposed);
            Assert.Empty(first.Paths);
            Assert.False(effect.CanRead);
            Assert.False(marker.CanRead);
            Assert.Throws<ObjectDisposedException>(() => first.Data);
            Assert.Equal(new byte[] { 1, 0, 0, 0 }, ReadNativeBytes(next.Data,
                ProbeOutboundDataObject.PreferredDropEffectFormat));
        });
    }

    [Fact]
    public void FactoryRevalidatesManifestAndSourceBeforeCreatingData()
    {
        wpf.Invoke(() =>
        {
            using var layout = ProbeOutboundTestLayout.Create();
            Assert.Throws<InvalidDataException>(() => ProbeOutboundDataObject.Create(layout.Output,
                ProbeFixtureProfile.Batch10, layout.Options.SourceRevision));
            Assert.Throws<InvalidDataException>(() => ProbeOutboundDataObject.Create(layout.Output,
                ProbeFixtureProfile.Single, "another-revision"));
            File.AppendAllText(layout.Bundle.OrderedPaths[0], "tampered");
            Assert.Throws<InvalidDataException>(() => layout.CreateData());
        });
    }

    [Fact]
    public void DataAndDisposalCannotCrossOwnerSta()
    {
        ProbeOutboundTestLayout? layout = null;
        ProbeOutboundDataObject? payload = null;
        wpf.Invoke(() => { layout = ProbeOutboundTestLayout.Create(); payload = layout.CreateData(); });
        try
        {
            Assert.Throws<InvalidOperationException>(() => payload!.Data);
            Assert.Throws<InvalidOperationException>(() => payload!.Dispose());
            Assert.Throws<InvalidOperationException>(() => layout!.CreateData());
            Assert.False(payload!.IsDisposed);
        }
        finally { wpf.Invoke(() => { payload!.Dispose(); layout!.Dispose(); }); }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(5)]
    public void ReceiverObservesRawPreferredMaskWithoutTurningItIntoMove(int value)
    {
        wpf.Invoke(() =>
        {
            using var stream = new MemoryStream([(byte)value, 0, 0, 0], writable: false);
            stream.Position = 3;
            var data = new DataObject(DataFormats.FileDrop, new[] { @"C:\probe\a.txt" }, false);
            data.SetData(ProbeOutboundDataObject.PreferredDropEffectFormat, stream, false);
            using var receiver = new ProbeFileDropReceiver(ProbeReceiverEffect.Copy, "test",
                new ProbeOutboundTestRecorder());
            receiver.Enter(data, DragDropEffects.All);
            Assert.Equal(DragDropEffects.Copy, receiver.Drop(data, DragDropEffects.All));
            Assert.Equal(new byte[] { (byte)value, 0, 0, 0 }, receiver.Report!.PreferredDropEffectBytes);
            Assert.Equal(1, receiver.Report.RawEffect);
            Assert.Equal(3, stream.Position);
            Assert.True(stream.CanRead); // The receiver borrows; it does not dispose foreign data.
        });
    }

    [Theory]
    [InlineData("null")]
    [InlineData("string")]
    [InlineData("short")]
    [InlineData("long")]
    [InlineData("present-throws")]
    [InlineData("get-throws")]
    public void MalformedOrThrowingPreferredDataFailsBeforeFileDropRead(string mode)
    {
        wpf.Invoke(() =>
        {
            var raw = new DataObject(DataFormats.FileDrop, new[] { @"C:\probe\a.txt" }, false);
            raw.SetData(ProbeOutboundDataObject.PreferredDropEffectFormat, new MemoryStream([1, 0, 0, 0]), false);
            var data = new ProbeOutboundTestData(raw)
            {
                ReadOverride = _ => mode switch
                {
                    "null" => null,
                    "string" => "1",
                    "short" => new byte[3],
                    "long" => new byte[5],
                    _ => throw new COMException("Failed preferred data read"),
                },
                BeforeCall = (operation, format, _) =>
                {
                    if (mode == "present-throws" && operation == "present" &&
                        format == ProbeOutboundDataObject.PreferredDropEffectFormat)
                        throw new COMException("Failed preferred format probe");
                },
            };
            var recorder = new ProbeOutboundTestRecorder();
            using var receiver = new ProbeFileDropReceiver(ProbeReceiverEffect.Copy, "test", recorder);
            receiver.Enter(data, DragDropEffects.Copy);
            Assert.Equal(DragDropEffects.None, receiver.Drop(data, DragDropEffects.Copy));
            var report = Assert.Single(recorder.Reports);
            Assert.Equal("Failed", report.Status);
            Assert.Equal("PreferredDropEffectReadFailed", report.Reason);
            Assert.Equal(0, report.FileDropReadCount);
            Assert.NotNull(report.Error);
            Assert.DoesNotContain(data.Calls, call => call.Operation == "get" && call.Format == DataFormats.FileDrop);
        });
    }

    [Theory]
    [InlineData("None")]
    [InlineData("Throw")]
    public void NonAcceptingPoliciesNeverFetchPreferredData(string policy)
    {
        wpf.Invoke(() =>
        {
            using var layout = ProbeOutboundTestLayout.Create();
            using var payload = layout.CreateData();
            var data = new ProbeOutboundTestData(payload.Data);
            using var receiver = new ProbeFileDropReceiver(Enum.Parse<ProbeReceiverEffect>(policy), "test",
                new ProbeOutboundTestRecorder());
            receiver.Enter(data, DragDropEffects.Copy);
            Assert.Equal(DragDropEffects.None, receiver.Drop(data, DragDropEffects.Copy));
            Assert.Equal(0, receiver.Report!.PreferredDropEffectReadCount);
            Assert.DoesNotContain(data.Calls, call => call.Operation == "get");
        });
    }

    private static T WithNativeData<T>(DataObject data, string format, Func<nint, T> read)
    {
        var native = (ComDataObject)data;
        var request = new FORMATETC
        {
            cfFormat = unchecked((short)DataFormats.GetDataFormat(format).Id),
            dwAspect = DVASPECT.DVASPECT_CONTENT, lindex = -1, tymed = TYMED.TYMED_HGLOBAL,
        };
        Assert.Equal(0, native.QueryGetData(ref request));
        native.GetData(ref request, out var medium);
        try
        {
            Assert.Equal(TYMED.TYMED_HGLOBAL, medium.tymed);
            Assert.NotEqual(nint.Zero, medium.unionmember);
            return read(medium.unionmember);
        }
        finally { Native.ReleaseStgMedium(ref medium); }
    }

    private static byte[] ReadNativeBytes(DataObject data, string format) => WithNativeData(data, format, handle =>
    {
        var length = checked((int)Native.GlobalSize(handle));
        Assert.InRange(length, 1, 1024 * 1024);
        var pointer = Native.GlobalLock(handle);
        Assert.NotEqual(nint.Zero, pointer);
        try
        {
            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return bytes;
        }
        finally { _ = Native.GlobalUnlock(handle); }
    });

    private static unsafe string[] ReadNativePaths(DataObject data) => WithNativeData(data, DataFormats.FileDrop, handle =>
    {
        var count = Native.DragQueryFile(handle, uint.MaxValue, null, 0);
        var paths = new string[count];
        for (uint index = 0; index < count; index++)
        {
            var length = Native.DragQueryFile(handle, index, null, 0);
            var buffer = new char[length + 1];
            fixed (char* pointer = buffer)
                Assert.Equal(length, Native.DragQueryFile(handle, index, pointer, (uint)buffer.Length));
            paths[index] = new string(buffer, 0, checked((int)length));
        }
        return paths;
    });

    private static partial class Native
    {
        // STGMEDIUM contains the COM release object; use runtime marshalling for this test-only ABI.
        [DllImport("ole32.dll", ExactSpelling = true)]
        internal static extern void ReleaseStgMedium(ref STGMEDIUM medium);
        [LibraryImport("kernel32.dll")]
        internal static partial nuint GlobalSize(nint handle);
        [LibraryImport("kernel32.dll")]
        internal static partial nint GlobalLock(nint handle);
        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GlobalUnlock(nint handle);
        [LibraryImport("shell32.dll", EntryPoint = "DragQueryFileW")]
        internal static unsafe partial uint DragQueryFile(nint handle, uint index, char* buffer, uint length);
    }
}
