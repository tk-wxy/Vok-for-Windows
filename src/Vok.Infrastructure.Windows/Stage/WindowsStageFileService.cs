using System.IO;
using Vok.Core.Stage;
using Vok.Infrastructure.Windows.Launcher;
using Vok.Infrastructure.Windows.Shell;

namespace Vok.Infrastructure.Windows.Stage;

public sealed class WindowsStageFileService : IStageFileService, IDisposable
{
    private readonly WindowsShellStaExecutor _executor;
    private readonly bool _ownsExecutor;
    private readonly Action<string> _openAction;
    private readonly Action<string> _revealAction;
    private int _disposed;

    public WindowsStageFileService()
        : this(
            new WindowsShellStaExecutor(),
            ownsExecutor: true,
            WindowsFileShellActions.Open,
            WindowsFileShellActions.RevealInExplorer)
    {
    }

    public WindowsStageFileService(WindowsShellStaExecutor executor)
        : this(
            executor,
            ownsExecutor: false,
            WindowsFileShellActions.Open,
            WindowsFileShellActions.RevealInExplorer)
    {
    }

    internal WindowsStageFileService(
        WindowsShellStaExecutor executor,
        Action<string> openAction,
        Action<string> revealAction)
        : this(
            executor,
            ownsExecutor: false,
            openAction,
            revealAction)
    {
    }

    private WindowsStageFileService(
        WindowsShellStaExecutor executor,
        bool ownsExecutor,
        Action<string> openAction,
        Action<string> revealAction)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
        _ownsExecutor = ownsExecutor;
        _openAction = openAction ?? throw new ArgumentNullException(nameof(openAction));
        _revealAction = revealAction ?? throw new ArgumentNullException(nameof(revealAction));
    }

    public Task<IReadOnlyList<StageTargetResolution>> ResolveAsync(
        IReadOnlyList<string> inputs,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        var snapshot = inputs.ToArray();
        return _executor.ExecuteAsync<IReadOnlyList<StageTargetResolution>>(
            () => snapshot.Select(ResolveOne).ToArray(),
            cancellationToken, diagnosticKind: "StageResolve");
    }

    public Task<StageFileAvailability> InspectAsync(
        StageItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        var targetValue = item.TargetValue;
        return _executor.ExecuteAsync(
            () => WindowsFileAvailabilityInspector.Inspect(targetValue) switch
            {
                WindowsFileAvailability.Unknown => StageFileAvailability.Unknown,
                WindowsFileAvailability.Available => StageFileAvailability.Available,
                WindowsFileAvailability.Missing => StageFileAvailability.Missing,
                WindowsFileAvailability.Unavailable => StageFileAvailability.Unavailable,
                _ => throw new InvalidOperationException("Unsupported file availability."),
            },
            cancellationToken, diagnosticKind: "StageAvailability",
            diagnosticItemId: item.Id, diagnosticRevision: item.Revision);
    }

    public Task OpenAsync(
        StageItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ExecuteAsync(
            () => _openAction(item.TargetValue),
            cancellationToken);
    }

    public Task RevealInExplorerAsync(
        StageItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ExecuteAsync(
            () => _revealAction(item.TargetValue),
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

    private static StageTargetResolution ResolveOne(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return Failed(
                input ?? string.Empty,
                StageTargetFailureKind.Empty,
                "目标为空。");
        }

        if (!Path.IsPathFullyQualified(input))
        {
            return Failed(
                input,
                StageTargetFailureKind.NotFullyQualified,
                "目标不是完全限定的文件系统路径。");
        }

        try
        {
            var reference = WindowsFileReferenceResolver.ResolveExisting(input);
            var kind = reference.Kind switch
            {
                WindowsFileReferenceKind.File => StageItemKind.File,
                WindowsFileReferenceKind.Directory => StageItemKind.Directory,
                WindowsFileReferenceKind.ShellLink => StageItemKind.ShellLink,
                _ => throw new ArgumentOutOfRangeException(nameof(reference)),
            };
            return new StageTargetResolution(
                input,
                new StageTargetDescriptor(
                    kind,
                    reference.NormalizedPath,
                    reference.NormalizedPath.ToUpperInvariant(),
                    reference.DisplayName),
                null,
                null);
        }
        catch (FileNotFoundException exception)
        {
            return Failed(input, StageTargetFailureKind.Missing, exception.Message);
        }
        catch (DirectoryNotFoundException exception)
        {
            return Failed(input, StageTargetFailureKind.Missing, exception.Message);
        }
        catch (UnauthorizedAccessException exception)
        {
            return Failed(input, StageTargetFailureKind.AccessDenied, exception.Message);
        }
        catch (ArgumentException exception)
        {
            return Failed(input, StageTargetFailureKind.InvalidPath, exception.Message);
        }
        catch (NotSupportedException exception)
        {
            return Failed(input, StageTargetFailureKind.Unsupported, exception.Message);
        }
        catch (IOException exception)
        {
            return Failed(input, StageTargetFailureKind.Unavailable, exception.Message);
        }
    }

    private static StageTargetResolution Failed(
        string input,
        StageTargetFailureKind failure,
        string error) =>
        new(input, null, failure, error);

    private Task ExecuteAsync(
        Action operation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        return _executor.ExecuteAsync(
            () =>
            {
                operation();
                return true;
            },
            cancellationToken, diagnosticKind: "StageAction");
    }
}
