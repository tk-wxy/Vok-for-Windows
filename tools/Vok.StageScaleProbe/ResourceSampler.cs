using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Vok.Features.Stage;

namespace Vok.StageScaleProbe;

internal sealed class ResourceSampler : IAsyncDisposable
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);
    private readonly ConcurrentQueue<ResourceSample> _samples = new();
    private readonly CancellationTokenSource _cancellation = new();
    private readonly Process _process = Process.GetCurrentProcess();
    private readonly Task _samplingTask;
    private string _phase = "startup";
    private int _step;
    private long _nextIndex;
    private int _stopped;

    internal ResourceSampler()
    {
        Capture();
        _samplingTask = Task.Run(SampleLoopAsync);
    }

    internal IReadOnlyList<ResourceSample> Samples => _samples.ToArray();

    internal void SetPhase(string phase, int step = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(phase);
        Volatile.Write(ref _phase, phase);
        Volatile.Write(ref _step, step);
    }

    internal void CaptureCheckpoint(string phase, int step = 0)
    {
        SetPhase(phase, step);
        Capture();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _cancellation.Cancel();
        try
        {
            await _samplingTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cancellation.IsCancellationRequested)
        {
        }

        Capture();
        _cancellation.Dispose();
        _process.Dispose();
    }

    internal static ResourceSummary Summarize(IReadOnlyList<ResourceSample> samples)
    {
        if (samples.Count == 0)
        {
            throw new ArgumentException("At least one resource sample is required.", nameof(samples));
        }

        return new ResourceSummary(
            SummarizeMetric(samples, static sample => sample.WorkingSetBytes),
            SummarizeMetric(samples, static sample => sample.PrivateBytes),
            SummarizeMetric(samples, static sample => sample.ManagedHeapBytes),
            SummarizeMetric(samples, static sample => sample.ManagedCommittedBytes),
            SummarizeMetric(samples, static sample => sample.HandleCount),
            SummarizeMetric(samples, static sample => sample.ThreadCount),
            SummarizeMetric(samples, static sample => sample.UserObjectCount),
            SummarizeMetric(samples, static sample => sample.GdiObjectCount));
    }

    private async Task SampleLoopAsync()
    {
        using var timer = new PeriodicTimer(SampleInterval);
        while (await timer
                   .WaitForNextTickAsync(_cancellation.Token)
                   .ConfigureAwait(false))
        {
            Capture();
        }
    }

    private void Capture()
    {
        _process.Refresh();
        var memory = GC.GetGCMemoryInfo();
        _samples.Enqueue(new ResourceSample(
            Index: Interlocked.Increment(ref _nextIndex),
            TimestampUtc: DateTimeOffset.UtcNow.ToString("O"),
            Phase: Volatile.Read(ref _phase),
            Step: Volatile.Read(ref _step),
            WorkingSetBytes: _process.WorkingSet64,
            PrivateBytes: _process.PrivateMemorySize64,
            ManagedHeapBytes: GC.GetTotalMemory(forceFullCollection: false),
            ManagedCommittedBytes: memory.TotalCommittedBytes,
            HandleCount: _process.HandleCount,
            ThreadCount: _process.Threads.Count,
            UserObjectCount: ProbeNativeMethods.GetGuiResources(_process.Handle, 1),
            GdiObjectCount: ProbeNativeMethods.GetGuiResources(_process.Handle, 0)));
    }

    private static MetricSummary SummarizeMetric<T>(
        IReadOnlyList<ResourceSample> samples,
        Func<ResourceSample, T> selector)
        where T : struct, IConvertible
    {
        var values = samples
            .Select(sample => Convert.ToDouble(selector(sample)))
            .ToArray();
        var tailOffset = values.Length / 2;
        var tail = values.AsSpan(tailOffset);
        return new MetricSummary(
            values.Length,
            values[0],
            values[^1],
            values.Min(),
            values.Max(),
            CalculateSlope(tail));
    }

    private static double CalculateSlope(ReadOnlySpan<double> values)
    {
        if (values.Length < 2)
        {
            return 0;
        }

        var meanX = (values.Length - 1) / 2.0;
        var meanY = 0.0;
        foreach (var value in values)
        {
            meanY += value;
        }

        meanY /= values.Length;
        var numerator = 0.0;
        var denominator = 0.0;
        for (var index = 0; index < values.Length; index++)
        {
            var deltaX = index - meanX;
            numerator += deltaX * (values[index] - meanY);
            denominator += deltaX * deltaX;
        }

        return denominator == 0 ? 0 : numerator / denominator;
    }
}

internal static class DisplayMetrics
{
    internal static DisplaySnapshot Capture(
        Window window,
        ScrollViewer scrollViewer,
        StageView view)
    {
        var handle = new WindowInteropHelper(window).Handle;
        var dpi = handle == IntPtr.Zero
            ? 96u
            : ProbeNativeMethods.GetDpiForWindow(handle);
        if (dpi == 0)
        {
            dpi = 96;
        }

        var monitor = ProbeNativeMethods.MonitorFromWindow(handle, 2);
        var monitorInfo = new ProbeNativeMethods.MonitorInfo
        {
            Size = (uint)Marshal.SizeOf<ProbeNativeMethods.MonitorInfo>(),
        };
        if (monitor == IntPtr.Zero ||
            !ProbeNativeMethods.GetMonitorInfo(monitor, ref monitorInfo))
        {
            monitorInfo.Monitor = default;
            monitorInfo.WorkArea = default;
        }

        return new DisplaySnapshot(
            window.ActualWidth,
            window.ActualHeight,
            scrollViewer.ViewportWidth,
            scrollViewer.ViewportHeight,
            view.CardLayoutProfile?.ColumnCount ?? 0,
            dpi,
            dpi / 96.0,
            monitorInfo.Monitor.Width,
            monitorInfo.Monitor.Height,
            monitorInfo.WorkArea.Width,
            monitorInfo.WorkArea.Height);
    }
}

internal static partial class ProbeNativeMethods
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeRect
    {
        internal int Left;
        internal int Top;
        internal int Right;
        internal int Bottom;

        internal readonly int Width => Right - Left;

        internal readonly int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MonitorInfo
    {
        internal uint Size;
        internal NativeRect Monitor;
        internal NativeRect WorkArea;
        internal uint Flags;
    }

    [LibraryImport("user32.dll")]
    internal static partial uint GetGuiResources(IntPtr process, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfo(
        IntPtr monitor,
        ref MonitorInfo monitorInfo);
}
