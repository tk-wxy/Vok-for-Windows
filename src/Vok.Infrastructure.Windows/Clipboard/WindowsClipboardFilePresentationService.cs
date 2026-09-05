using Vok.Core.Clipboard;
using Vok.Infrastructure.Windows.Launcher;
using Vok.Infrastructure.Windows.Shell;

namespace Vok.Infrastructure.Windows.Clipboard;

public sealed class WindowsClipboardFilePresentationService
    : IClipboardFilePresentationService
{
    private const int IconCacheCapacity = 256;
    private readonly WindowsShellStaExecutor _executor;
    private readonly bool _ownsExecutor;
    private readonly Func<string, int, byte[]?> _iconExtractor;
    private readonly Dictionary<string, byte[]> _iconCache =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _iconCacheOrder = new();
    private int _disposed;

    public WindowsClipboardFilePresentationService()
        : this(
            new WindowsShellStaExecutor(),
            ownsExecutor: true,
            WindowsLauncherShellService.TryExtractShellIcon)
    {
    }

    public WindowsClipboardFilePresentationService(WindowsShellStaExecutor executor)
        : this(
            executor,
            ownsExecutor: false,
            WindowsLauncherShellService.TryExtractShellIcon)
    {
    }

    internal WindowsClipboardFilePresentationService(
        WindowsShellStaExecutor executor,
        Func<string, int, byte[]?> iconExtractor)
        : this(executor, ownsExecutor: false, iconExtractor)
    {
    }

    private WindowsClipboardFilePresentationService(
        WindowsShellStaExecutor executor,
        bool ownsExecutor,
        Func<string, int, byte[]?> iconExtractor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _ownsExecutor = ownsExecutor;
        _iconExtractor = iconExtractor ?? throw new ArgumentNullException(nameof(iconExtractor));
    }

    public Task<ClipboardFilePresentation> InspectAsync(
        IReadOnlyList<ClipboardFileReference> files,
        int pixelSize,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (files.Count == 0)
        {
            throw new ArgumentException(
                "File presentation requires at least one reference.",
                nameof(files));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(pixelSize, 16);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(pixelSize, 256);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        return _executor.ExecuteAsync(
            () => Inspect(files.ToArray(), pixelSize),
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

    private ClipboardFilePresentation Inspect(
        IReadOnlyList<ClipboardFileReference> files,
        int pixelSize)
    {
        var availability = ClipboardFileAvailability.Available;
        foreach (var file in files)
        {
            availability = Combine(
                availability,
                GetAvailability(file.CanonicalPath));
            if (availability is ClipboardFileAvailability.Missing)
            {
                break;
            }
        }

        byte[]? icon = null;
        if (availability is ClipboardFileAvailability.Available)
        {
            var first = files[0];
            var cacheKey = $"{first.CanonicalPath}|{pixelSize}";
            if (!_iconCache.TryGetValue(cacheKey, out icon))
            {
                icon = _iconExtractor(
                    first.CanonicalPath,
                    pixelSize);
                if (icon is null && pixelSize < 96)
                {
                    icon = _iconExtractor(
                        first.CanonicalPath,
                        96);
                }

                if (icon is not null)
                {
                    while (_iconCache.Count >= IconCacheCapacity &&
                           _iconCacheOrder.TryDequeue(out var oldest))
                    {
                        _iconCache.Remove(oldest);
                    }

                    _iconCache[cacheKey] = icon;
                    _iconCacheOrder.Enqueue(cacheKey);
                }
            }
        }

        return new ClipboardFilePresentation(icon, availability);
    }

    private static ClipboardFileAvailability Combine(
        ClipboardFileAvailability current,
        ClipboardFileAvailability next)
    {
        if (current is ClipboardFileAvailability.Missing ||
            next is ClipboardFileAvailability.Missing)
        {
            return ClipboardFileAvailability.Missing;
        }

        if (current is ClipboardFileAvailability.Unavailable ||
            next is ClipboardFileAvailability.Unavailable)
        {
            return ClipboardFileAvailability.Unavailable;
        }

        if (current is ClipboardFileAvailability.Unknown ||
            next is ClipboardFileAvailability.Unknown)
        {
            return ClipboardFileAvailability.Unknown;
        }

        return ClipboardFileAvailability.Available;
    }

    private static ClipboardFileAvailability GetAvailability(string path)
        => WindowsFileAvailabilityInspector.Inspect(path) switch
        {
            WindowsFileAvailability.Unknown => ClipboardFileAvailability.Unknown,
            WindowsFileAvailability.Available => ClipboardFileAvailability.Available,
            WindowsFileAvailability.Missing => ClipboardFileAvailability.Missing,
            WindowsFileAvailability.Unavailable => ClipboardFileAvailability.Unavailable,
            _ => throw new InvalidOperationException("Unsupported file availability."),
        };
}
