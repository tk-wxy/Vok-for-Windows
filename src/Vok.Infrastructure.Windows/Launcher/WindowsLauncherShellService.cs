using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Vok.Core.Launcher;

namespace Vok.Infrastructure.Windows.Launcher;

public sealed partial class WindowsLauncherShellService : ILauncherShellService
{
    private const int QueueCapacity = 128;
    private const int IconCacheCapacity = 256;
    private static readonly Guid ShellItemImageFactoryId =
        new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    private readonly BlockingCollection<IWorkItem> _queue = new(QueueCapacity);
    private readonly Dictionary<string, LauncherIconData> _iconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _iconCacheOrder = new();
    private readonly Thread _thread;
    private int _disposed;

    public WindowsLauncherShellService()
    {
        _thread = new Thread(Run)
        {
            IsBackground = true,
            Name = "Vok Shell/COM STA",
        };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    public Task<IReadOnlyList<LauncherTargetResolution>> ResolveAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var snapshot = inputs.ToArray();
        return ExecuteAsync<IReadOnlyList<LauncherTargetResolution>>(
            () => snapshot.Select(ResolveOne).ToArray(),
            cancellationToken);
    }

    public Task<LauncherIconData?> GetIconAsync(
        LauncherItem item,
        int pixelSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentOutOfRangeException.ThrowIfLessThan(pixelSize, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pixelSize, 256);
        return ExecuteAsync(
            () => GetIcon(item, pixelSize),
            cancellationToken);
    }

    public Task OpenAsync(
        LauncherItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ExecuteAsync(
            () =>
            {
                var started = Process.Start(new ProcessStartInfo
                {
                    FileName = item.TargetValue,
                    UseShellExecute = true,
                });
                if (started is null)
                {
                    throw new InvalidOperationException(
                        $"Windows Shell 未能启动目标：{item.TargetValue}");
                }

                started.Dispose();
                return true;
            },
            cancellationToken);
    }

    public Task RevealInExplorerAsync(
        LauncherItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        if (!item.CanRevealInExplorer)
        {
            return Task.FromException(new InvalidOperationException(
                "This launcher target has no file-system location."));
        }

        return ExecuteAsync(
            () =>
            {
                RevealInExplorer(item.TargetValue);
                return true;
            },
            cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _queue.CompleteAdding();
        _thread.Join();
        _queue.Dispose();
    }

    private Task<T> ExecuteAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        cancellationToken.ThrowIfCancellationRequested();

        var item = new WorkItem<T>(operation, cancellationToken);
        try
        {
            if (!_queue.TryAdd(item))
            {
                throw new InvalidOperationException("The Shell/COM queue is full.");
            }
        }
        catch
        {
            item.Abandon();
            throw;
        }

        return item.Task;
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            item.Execute();
        }
    }

    private static LauncherTargetResolution ResolveOne(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new LauncherTargetResolution(input ?? string.Empty, null, "目标为空。");
        }

        try
        {
            var fullPath = Path.GetFullPath(input.Trim());
            var attributes = File.GetAttributes(fullPath);
            var isDirectory = (attributes & FileAttributes.Directory) != 0;
            var kind = isDirectory
                ? LauncherItemKind.Directory
                : string.Equals(
                    Path.GetExtension(fullPath),
                    ".lnk",
                    StringComparison.OrdinalIgnoreCase)
                    ? LauncherItemKind.ShellLink
                    : LauncherItemKind.File;
            var normalizedPath = isDirectory
                ? Path.TrimEndingDirectorySeparator(fullPath)
                : fullPath;
            var name = GetDisplayName(normalizedPath, kind);
            var fingerprint = GetFingerprint(normalizedPath, attributes);
            var canonicalKey = string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)kind}:{normalizedPath}");
            return new LauncherTargetResolution(
                input,
                new LauncherTargetDescriptor(
                    kind,
                    normalizedPath,
                    canonicalKey,
                    name,
                    fingerprint),
                null);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
                NotSupportedException)
        {
            return new LauncherTargetResolution(input, null, exception.Message);
        }
    }

    private LauncherIconData? GetIcon(LauncherItem item, int pixelSize)
    {
        var fingerprint = GetFingerprintForItem(item);
        var key = string.Create(
            CultureInfo.InvariantCulture,
            $"{item.CanonicalKey}|{fingerprint}|{pixelSize}");
        if (_iconCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var png = TryExtractShellIcon(item.TargetValue, pixelSize);
        if (png is null)
        {
            return null;
        }

        var icon = new LauncherIconData(png, fingerprint, pixelSize);
        while (_iconCache.Count >= IconCacheCapacity &&
               _iconCacheOrder.TryDequeue(out var oldest))
        {
            _iconCache.Remove(oldest);
        }

        _iconCache[key] = icon;
        _iconCacheOrder.Enqueue(key);
        return icon;
    }

    private static byte[]? TryExtractShellIcon(string target, int pixelSize)
    {
        nint factoryPointer = 0;
        object? factoryObject = null;
        nint bitmapHandle = 0;
        try
        {
            var iid = ShellItemImageFactoryId;
            var hr = SHCreateItemFromParsingName(target, 0, in iid, out factoryPointer);
            if (hr < 0 || factoryPointer == 0)
            {
                return null;
            }

            factoryObject = Marshal.GetObjectForIUnknown(factoryPointer);
            var factory = (IShellItemImageFactory)factoryObject;
            hr = factory.GetImage(
                new NativeSize(pixelSize, pixelSize),
                ShellItemImageFlags.IconOnly | ShellItemImageFlags.BiggerSizeOk,
                out bitmapHandle);
            if (hr < 0 || bitmapHandle == 0)
            {
                return null;
            }

            var source = Imaging.CreateBitmapSourceFromHBitmap(
                bitmapHandle,
                0,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        catch (COMException)
        {
            return null;
        }
        finally
        {
            if (bitmapHandle != 0)
            {
                _ = DeleteObject(bitmapHandle);
            }

            if (factoryObject is not null)
            {
                _ = Marshal.FinalReleaseComObject(factoryObject);
            }

            if (factoryPointer != 0)
            {
                _ = Marshal.Release(factoryPointer);
            }
        }
    }

    private static void RevealInExplorer(string target)
    {
        var root = Path.GetPathRoot(target);
        if (string.Equals(
                Path.TrimEndingDirectorySeparator(target),
                Path.TrimEndingDirectorySeparator(root ?? string.Empty),
                StringComparison.OrdinalIgnoreCase))
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = target,
                UseShellExecute = true,
            });
            return;
        }

        var pidl = ILCreateFromPath(target);
        if (pidl == 0)
        {
            throw new InvalidOperationException($"无法解析路径：{target}");
        }

        try
        {
            var hr = SHOpenFolderAndSelectItems(pidl, 0, 0, 0);
            Marshal.ThrowExceptionForHR(hr);
        }
        finally
        {
            ILFree(pidl);
        }
    }

    private static string GetDisplayName(string path, LauncherItemKind kind)
    {
        var name = Path.GetFileName(path);
        if (kind is LauncherItemKind.ShellLink &&
            name.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string GetFingerprint(string path, FileAttributes attributes)
    {
        var lastWrite = File.GetLastWriteTimeUtc(path).Ticks;
        var length = (attributes & FileAttributes.Directory) != 0
            ? 0
            : new FileInfo(path).Length;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{lastWrite}:{length}:{(int)attributes}");
    }

    private static string GetFingerprintForItem(LauncherItem item)
    {
        try
        {
            return GetFingerprint(item.TargetValue, File.GetAttributes(item.TargetValue));
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
                NotSupportedException)
        {
            return $"unavailable:{item.Revision}";
        }
    }

    private interface IWorkItem
    {
        void Execute();
    }

    private sealed class WorkItem<T> : IWorkItem
    {
        private readonly TaskCompletionSource<T> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly Func<T> _operation;
        private readonly CancellationTokenRegistration _cancellationRegistration;
        private int _claim;

        public WorkItem(Func<T> operation, CancellationToken cancellationToken)
        {
            _operation = operation;
            _cancellationRegistration = cancellationToken.Register(() =>
            {
                if (Interlocked.CompareExchange(ref _claim, 2, 0) == 0)
                {
                    _completion.TrySetCanceled(cancellationToken);
                }
            });
        }

        public Task<T> Task => _completion.Task;

        public void Abandon()
        {
            _ = Interlocked.CompareExchange(ref _claim, 2, 0);
            _cancellationRegistration.Dispose();
        }

        public void Execute()
        {
            // The Shell thread and cancellation compete for one pre-side-effect claim.
            if (Interlocked.CompareExchange(ref _claim, 1, 0) != 0)
            {
                _cancellationRegistration.Dispose();
                return;
            }

            _cancellationRegistration.Dispose();
            try
            {
                _completion.TrySetResult(_operation());
            }
            catch (Exception exception)
            {
                _completion.TrySetException(exception);
            }
        }
    }

    [Flags]
    private enum ShellItemImageFlags : uint
    {
        BiggerSizeOk = 0x1,
        IconOnly = 0x4,
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeSize(int Width, int Height);

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(
            NativeSize size,
            ShellItemImageFlags flags,
            out nint bitmapHandle);
    }

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int SHCreateItemFromParsingName(
        string path,
        nint bindContext,
        in Guid interfaceId,
        out nint shellItem);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint objectHandle);

    [LibraryImport("shell32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint ILCreateFromPath(string path);

    [LibraryImport("shell32.dll")]
    private static partial void ILFree(nint itemIdList);

    [LibraryImport("shell32.dll")]
    private static partial int SHOpenFolderAndSelectItems(
        nint folderItemIdList,
        uint itemCount,
        nint childItemIdLists,
        uint flags);
}
