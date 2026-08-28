using System.Runtime.InteropServices;
using System.Text;
using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Clipboard;
using Xunit;

namespace Vok.IntegrationTests.Clipboard;

public sealed class WindowsClipboardPlatformServiceTests
{
    [Fact]
    public async Task RealListenerStartsOnDedicatedStaWithoutCapturingExistingContent()
    {
        var service = new WindowsClipboardPlatformService();
        await service.StartAsync(TestContext.Current.CancellationToken);

        var running = service.GetDiagnosticSnapshot();
        Assert.True(running.IsRunning);
        Assert.True(running.IsListenerRegistered);
        Assert.True(running.IsStaThread);
        Assert.Equal(0, running.CaptureAttemptCount);
        Assert.Equal(0, running.CapturedSnapshotCount);

        service.Dispose();

        var stopped = service.GetDiagnosticSnapshot();
        Assert.False(stopped.IsRunning);
        Assert.False(stopped.IsListenerRegistered);
        Assert.False(stopped.HasPendingCapture);
    }

    [Fact]
    public async Task DuplicateSignalsAreMergedAndBusyRetriesStayOnClipboardSta()
    {
        using var native = new ServiceFakeWindowsClipboardNativeApi(initialSequence: 70);
        native.OpenFailuresRemaining = 2;
        var options = new ClipboardCaptureOptions(
            busyRetrySchedule:
            [
                TimeSpan.FromMilliseconds(10),
                TimeSpan.FromMilliseconds(10),
            ]);
        using var service = new WindowsClipboardPlatformService(
            options,
            native,
            Guid.NewGuid());
        await service.StartAsync(TestContext.Current.CancellationToken);
        native.SetUnicodeText("captured after retry", sequence: 71);
        await using var snapshots = service
            .WatchAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(ClipboardListenerNativeMethods.PostWindowMessage(
            service.ListenerWindowHandle,
            ClipboardListenerNativeMethods.ClipboardUpdateMessage));
        Assert.True(ClipboardListenerNativeMethods.PostWindowMessage(
            service.ListenerWindowHandle,
            ClipboardListenerNativeMethods.ClipboardUpdateMessage));
        Assert.True(await snapshots.MoveNextAsync());

        var result = snapshots.Current;
        Assert.Equal(ClipboardCaptureDisposition.Captured, result.Disposition);
        Assert.Equal(71u, result.RuntimeSequence);
        var text = Assert.Single(result.Formats);
        Assert.Equal(
            "captured after retry\0",
            Encoding.Unicode.GetString(text.Payload.Span));
        var diagnostics = service.GetDiagnosticSnapshot();
        Assert.Equal(2, diagnostics.SignalCount);
        Assert.Equal(1, diagnostics.DuplicateSignalCount);
        Assert.Equal(3, diagnostics.CaptureAttemptCount);
        Assert.Equal(2, diagnostics.BusyRetryCount);
        Assert.Equal(0, diagnostics.BusyExhaustedCount);
        Assert.Equal(1, diagnostics.CapturedSnapshotCount);
        Assert.False(diagnostics.HasPendingCapture);
        _ = Assert.Single(native.DistinctClipboardThreadIds);
    }

    [Fact]
    public async Task BusyRetryExhaustionPublishesOneTerminalResultAndReleasesSequence()
    {
        using var native = new ServiceFakeWindowsClipboardNativeApi(initialSequence: 80);
        native.OpenFailuresRemaining = 10;
        var options = new ClipboardCaptureOptions(
            busyRetrySchedule: [TimeSpan.FromMilliseconds(5)]);
        using var service = new WindowsClipboardPlatformService(
            options,
            native,
            Guid.NewGuid());
        await service.StartAsync(TestContext.Current.CancellationToken);
        native.SetUnicodeText("busy", sequence: 81);
        await using var snapshots = service
            .WatchAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        Assert.True(ClipboardListenerNativeMethods.PostWindowMessage(
            service.ListenerWindowHandle,
            ClipboardListenerNativeMethods.ClipboardUpdateMessage));
        Assert.True(await snapshots.MoveNextAsync());

        Assert.Equal(
            ClipboardCaptureDisposition.Busy,
            snapshots.Current.Disposition);
        var diagnostics = service.GetDiagnosticSnapshot();
        Assert.Equal(2, diagnostics.CaptureAttemptCount);
        Assert.Equal(1, diagnostics.BusyRetryCount);
        Assert.Equal(1, diagnostics.BusyExhaustedCount);
        Assert.False(diagnostics.HasPendingCapture);
        Assert.Equal(81u, diagnostics.LastCompletedSequence);
    }

    [Fact]
    public async Task ClipboardChangeBetweenInitialWatermarkAndListenerRegistrationIsCaptured()
    {
        using var native = new ServiceFakeWindowsClipboardNativeApi(initialSequence: 90);
        native.SetUnicodeText("startup race", sequence: 91);
        native.ScriptSequenceReads(90, 91);
        using var service = new WindowsClipboardPlatformService(
            new ClipboardCaptureOptions(),
            native,
            Guid.NewGuid());
        await using var snapshots = service
            .WatchAsync(TestContext.Current.CancellationToken)
            .GetAsyncEnumerator(TestContext.Current.CancellationToken);

        await service.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(await snapshots.MoveNextAsync());

        Assert.Equal(91u, snapshots.Current.RuntimeSequence);
        Assert.Equal(
            ClipboardCaptureDisposition.Captured,
            snapshots.Current.Disposition);
        var diagnostics = service.GetDiagnosticSnapshot();
        Assert.Equal(1, diagnostics.SignalCount);
        Assert.Equal(1, diagnostics.CapturedSnapshotCount);
        Assert.False(diagnostics.HasPendingCapture);
    }

    [Fact]
    public async Task DisposeBeforeStartIsIdempotent()
    {
        var service = new WindowsClipboardPlatformService();

        service.Dispose();
        service.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => service.StartAsync(TestContext.Current.CancellationToken));
    }

    private sealed class ServiceFakeWindowsClipboardNativeApi
        : IWindowsClipboardNativeApi, IDisposable
    {
        private readonly Dictionary<string, uint> _registered =
            new(StringComparer.Ordinal);
        private readonly byte[] _payload;
        private readonly GCHandle _payloadPin;
        private readonly object _gate = new();
        private readonly Queue<uint> _scriptedSequenceReads = [];
        private uint _nextRegisteredFormat = 0xC000;
        private uint _sequence;
        private int _lastError;
        private int _openFailuresRemaining;
        private int _payloadLength;
        private bool _isOpen;

        public ServiceFakeWindowsClipboardNativeApi(uint initialSequence)
        {
            _sequence = initialSequence;
            _payload = new byte[512];
            _payloadPin = GCHandle.Alloc(_payload, GCHandleType.Pinned);
        }

        public int OpenFailuresRemaining
        {
            get => Volatile.Read(ref _openFailuresRemaining);
            set => Volatile.Write(ref _openFailuresRemaining, value);
        }

        public HashSet<int> DistinctClipboardThreadIds { get; } = [];

        public uint GetSequenceNumber()
        {
            lock (_gate)
            {
                if (_scriptedSequenceReads.TryDequeue(out var scripted))
                {
                    return scripted;
                }

                return _sequence;
            }
        }

        public uint RegisterFormat(string name)
        {
            lock (_gate)
            {
                if (_registered.TryGetValue(name, out var format))
                {
                    return format;
                }

                format = _nextRegisteredFormat++;
                _registered.Add(name, format);
                return format;
            }
        }

        public bool Open(nint ownerWindow)
        {
            RecordClipboardThread();
            if (Interlocked.Decrement(ref _openFailuresRemaining) >= 0)
            {
                _lastError = 5;
                return false;
            }

            _isOpen = true;
            return true;
        }

        public bool Close()
        {
            RecordClipboardThread();
            Assert.True(_isOpen);
            _isOpen = false;
            return true;
        }

        public uint EnumerateFormats(uint previousFormat)
        {
            RecordClipboardThread();
            Assert.True(_isOpen);
            return previousFormat == 0 ? ClipboardKnownFormats.UnicodeText : 0;
        }

        public string? GetFormatName(uint format) => null;

        public nint GetData(uint format)
        {
            RecordClipboardThread();
            Assert.True(_isOpen);
            Assert.Equal(ClipboardKnownFormats.UnicodeText, format);
            return (nint)1;
        }

        public nuint GetGlobalSize(nint handle)
        {
            RecordClipboardThread();
            return checked((nuint)_payloadLength);
        }

        public nint LockGlobal(nint handle)
        {
            RecordClipboardThread();
            return _payloadPin.AddrOfPinnedObject();
        }

        public void UnlockGlobal(nint handle) => RecordClipboardThread();

        public void ResetLastError() => _lastError = 0;

        public int GetLastError() => _lastError;

        public void SetUnicodeText(string text, uint sequence)
        {
            var bytes = Encoding.Unicode.GetBytes(text + '\0');
            Assert.True(bytes.Length <= _payload.Length);
            Array.Clear(_payload);
            bytes.CopyTo(_payload, 0);
            _payloadLength = bytes.Length;
            lock (_gate)
            {
                _sequence = sequence;
            }
        }

        public void ScriptSequenceReads(params uint[] sequences)
        {
            lock (_gate)
            {
                foreach (var sequence in sequences)
                {
                    _scriptedSequenceReads.Enqueue(sequence);
                }
            }
        }

        public void Dispose()
        {
            if (_payloadPin.IsAllocated)
            {
                _payloadPin.Free();
            }
        }

        private void RecordClipboardThread()
        {
            lock (_gate)
            {
                DistinctClipboardThreadIds.Add(Environment.CurrentManagedThreadId);
            }
        }
    }
}
