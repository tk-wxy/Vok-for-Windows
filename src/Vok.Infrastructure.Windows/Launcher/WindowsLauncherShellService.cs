using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using Vok.Core.Launcher;
using Vok.Infrastructure.Windows.Shell;

namespace Vok.Infrastructure.Windows.Launcher;

public sealed partial class WindowsLauncherShellService : ILauncherShellService
{
    private const int IconCacheCapacity = 256;
    private static readonly Guid ShellItemImageFactoryId =
        new("BCC18B79-BA16-442F-80C4-8A59C30C463B");

    private readonly WindowsShellStaExecutor _executor;
    private readonly bool _ownsExecutor;
    private readonly Dictionary<string, LauncherIconData> _iconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _iconCacheOrder = new();
    private int _disposed;

    public WindowsLauncherShellService()
        : this(new WindowsShellStaExecutor(), ownsExecutor: true)
    {
    }

    public WindowsLauncherShellService(WindowsShellStaExecutor executor)
        : this(executor, ownsExecutor: false)
    {
    }

    private WindowsLauncherShellService(
        WindowsShellStaExecutor executor,
        bool ownsExecutor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _ownsExecutor = ownsExecutor;
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

        if (_ownsExecutor)
        {
            _executor.Dispose();
        }
    }

    private Task<T> ExecuteAsync<T>(
        Func<T> operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        return _executor.ExecuteAsync(operation, cancellationToken);
    }

    private static LauncherTargetResolution ResolveOne(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return new LauncherTargetResolution(input ?? string.Empty, null, "目标为空。");
        }

        try
        {
            var reference = WindowsFileReferenceResolver.ResolveExisting(input.Trim());
            var kind = reference.Kind switch
            {
                WindowsFileReferenceKind.File => LauncherItemKind.File,
                WindowsFileReferenceKind.Directory => LauncherItemKind.Directory,
                WindowsFileReferenceKind.ShellLink => LauncherItemKind.ShellLink,
                _ => throw new ArgumentOutOfRangeException(nameof(reference)),
            };
            var canonicalKey = string.Create(
                CultureInfo.InvariantCulture,
                $"{(int)kind}:{reference.NormalizedPath}");
            return new LauncherTargetResolution(
                input,
                new LauncherTargetDescriptor(
                    kind,
                    reference.NormalizedPath,
                    canonicalKey,
                    reference.DisplayName,
                    reference.Fingerprint),
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

    internal static byte[]? TryExtractShellIcon(string target, int pixelSize)
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

    private static string GetFingerprintForItem(LauncherItem item)
    {
        try
        {
            return WindowsFileReferenceResolver.GetFingerprint(item.TargetValue);
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException or UnauthorizedAccessException or
                NotSupportedException)
        {
            return $"unavailable:{item.Revision}";
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
