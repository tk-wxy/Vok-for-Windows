using System.Buffers;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Vok.Core.Clipboard;
using Vok.Core.Workspace;
using Vok.Infrastructure.Windows.Launcher;

namespace Vok.Infrastructure.Windows.Clipboard;

public sealed class WindowsClipboardImageDestinationService :
    IClipboardImageDestinationService
{
    private readonly WindowsShellStaExecutor _executor;
    private int _disposed;

    public WindowsClipboardImageDestinationService(
        WindowsShellStaExecutor executor)
    {
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public Task<IClipboardImageDestinationClaim?> ResolveAsync(
        ForegroundTarget target,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return _executor.ExecuteAsync<IClipboardImageDestinationClaim?>(
            () =>
            {
                try
                {
                    var resolved = TryResolve(target);
                    return resolved is null
                        ? null
                        : new DestinationClaim(
                            target,
                            resolved.Kind,
                            resolved.Path);
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(
                        "Clipboard image destination resolution failed safely: {0}",
                        exception);
                    return null;
                }
            },
            cancellationToken);
    }

    public async Task<ClipboardImageFileCreationResult> CreateAsync(
        IClipboardImageDestinationClaim destination,
        ClipboardImageExportMaterialization materialization,
        string requestedFileName,
        IClipboardImageFileCreationAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentNullException.ThrowIfNull(authorization);
        ValidateFileName(requestedFileName);
        if (destination is not DestinationClaim claim || !claim.TryBegin())
        {
            return ClipboardImageFileCreationResult.StaleTarget;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return await CreateFromCanonicalAsync(
                materialization,
                stagedPath => CreateCore(
                    claim,
                    stagedPath,
                    requestedFileName,
                    materialization,
                    authorization,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);
    }

    internal Task<ClipboardImageFileCreationResult> CreateInDirectoryForTestingAsync(
        ClipboardImageExportMaterialization materialization,
        string destinationDirectory,
        string requestedFileName,
        IClipboardImageFileCreationAuthorization authorization,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(materialization);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDirectory);
        ArgumentNullException.ThrowIfNull(authorization);
        ValidateFileName(requestedFileName);
        var normalizedDestination =
            WindowsClipboardImageDestinationInterop.NormalizePath(
                destinationDirectory);
        if (!Directory.Exists(normalizedDestination))
        {
            throw new DirectoryNotFoundException(
                "The test image destination directory does not exist.");
        }

        return CreateFromCanonicalAsync(
            materialization,
            stagedPath =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!authorization.TryClaimFileCreation())
                {
                    return ClipboardImageFileCreationResult.StaleTarget;
                }

                return CopyAndValidate(
                    stagedPath,
                    normalizedDestination,
                    requestedFileName,
                    materialization);
            },
            cancellationToken);
    }

    public void Dispose() => Interlocked.Exchange(ref _disposed, 1);

    private async Task<ClipboardImageFileCreationResult> CreateFromCanonicalAsync(
        ClipboardImageExportMaterialization materialization,
        Func<string, ClipboardImageFileCreationResult> createFromStagedPath,
        CancellationToken cancellationToken)
    {
        var temporaryBase = WindowsClipboardImageDestinationInterop.NormalizePath(
            Path.Combine(Path.GetTempPath(), "Vok", "ImageDestination"));
        var temporaryRoot = Path.Combine(
            temporaryBase,
            Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        var stagedPath = Path.Combine(temporaryRoot, "canonical.png");
        var cleaned = false;
        ClipboardImageFileCreationResult result;
        try
        {
            Directory.CreateDirectory(temporaryRoot);
            await StageCanonicalPngAsync(
                    materialization,
                    stagedPath,
                    cancellationToken)
                .ConfigureAwait(false);
            result = await _executor.ExecuteAsync(
                    () => createFromStagedPath(stagedPath),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                if (IsOwnedTemporaryRoot(temporaryRoot, temporaryBase) &&
                    Directory.Exists(temporaryRoot))
                {
                    Directory.Delete(temporaryRoot, recursive: true);
                }

                cleaned = !Directory.Exists(temporaryRoot);
            }
            catch (Exception exception)
            {
                Trace.TraceWarning(
                    "Clipboard image destination staging cleanup failed: {0}",
                    exception);
            }
        }

        return result with { TemporaryFilesCleaned = cleaned };
    }

    private static ClipboardImageFileCreationResult CreateCore(
        DestinationClaim claim,
        string stagedPath,
        string requestedFileName,
        ClipboardImageExportMaterialization materialization,
        IClipboardImageFileCreationAuthorization authorization,
        CancellationToken cancellationToken)
    {
        var current = TryResolve(claim.Target);
        if (current is null ||
            current.Kind != claim.Kind ||
            !PathsEqual(current.Path, claim.Path))
        {
            return ClipboardImageFileCreationResult.StaleTarget;
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!authorization.TryClaimFileCreation())
        {
            return ClipboardImageFileCreationResult.StaleTarget;
        }

        return CopyAndValidate(
            stagedPath,
            claim.Path,
            requestedFileName,
            materialization);
    }

    private static ClipboardImageFileCreationResult CopyAndValidate(
        string stagedPath,
        string destinationDirectory,
        string requestedFileName,
        ClipboardImageExportMaterialization materialization)
    {
        var createdPath = CopyOne(
            stagedPath,
            destinationDirectory,
            requestedFileName);
        var createdDirectory = Path.GetDirectoryName(createdPath);
        if (createdDirectory is null ||
            !PathsEqual(createdDirectory, destinationDirectory) ||
            !string.Equals(
                Path.GetExtension(createdPath),
                ".png",
                StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(createdPath))
        {
            throw new InvalidDataException(
                "The Shell copy result was outside the authorized destination.");
        }

        var file = new FileInfo(createdPath);
        if (file.Length != materialization.CanonicalPngByteLength ||
            !string.Equals(
                HashFile(createdPath),
                materialization.AssetHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The created PNG does not match the verified canonical asset.");
        }

        return new ClipboardImageFileCreationResult(
            ClipboardImageFileCreationStatus.Created,
            Path.GetFileName(createdPath),
            TemporaryFilesCleaned: false);
    }

    private static ResolvedDestination? TryResolve(ForegroundTarget target)
    {
        EnsureSta();
        if (!TargetIdentityMatches(target))
        {
            return null;
        }

        var explorerPaths = EnumerateActiveExplorerPaths(target);
        if (explorerPaths.Count == 1)
        {
            return new ResolvedDestination(
                ClipboardImageDestinationKind.FileSystemFolder,
                explorerPaths[0]);
        }

        if (explorerPaths.Count > 1)
        {
            return null;
        }

        var targetClass = WindowsClipboardImageDestinationInterop.GetWindowClass(
            target.WindowHandle);
        var shellWindow = WindowsClipboardImageDestinationInterop.ShellWindow;
        var shellWindowMatch = target.WindowHandle == shellWindow;
        var shellProcessMatch = shellWindow != 0 &&
            WindowsClipboardImageDestinationInterop
                .GetWindowIdentity(shellWindow)
                .ProcessId == target.ProcessId;
        var desktopClass = targetClass is "Progman" or "WorkerW";
        var desktopViewObserved =
            string.Equals(targetClass, "SHELLDLL_DefView", StringComparison.Ordinal) ||
            WindowsClipboardImageDestinationInterop.ContainsDescendantClass(
                target.WindowHandle,
                "SHELLDLL_DefView");
        if (!shellWindowMatch &&
            !(shellProcessMatch && desktopClass && desktopViewObserved))
        {
            return null;
        }

        var desktopPath =
            WindowsClipboardImageDestinationInterop.GetDesktopKnownFolderPath();
        return Directory.Exists(desktopPath)
            ? new ResolvedDestination(
                ClipboardImageDestinationKind.Desktop,
                desktopPath)
            : null;
    }

    private static IReadOnlyList<string> EnumerateActiveExplorerPaths(
        ForegroundTarget target)
    {
        object? shellWindows = null;
        try
        {
            shellWindows = WindowsClipboardImageDestinationInterop.CreateComObject(
                WindowsClipboardImageDestinationInterop.ShellWindowsClassId);
            dynamic windows = shellWindows;
            var count = Convert.ToInt32(windows.Count, CultureInfo.InvariantCulture);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var index = 0; index < count; index++)
            {
                object? browser = null;
                try
                {
                    browser = windows.Item(index);
                    if (browser is null ||
                        GetBrowserWindow(browser) != target.WindowHandle)
                    {
                        continue;
                    }

                    var active = TryGetActiveViewPath(
                        browser,
                        target.FocusedWindowHandle);
                    if (active.FocusWithinView && active.Path is not null)
                    {
                        paths.Add(active.Path);
                    }
                }
                catch (Exception exception)
                {
                    Trace.TraceWarning(
                        "An Explorer window was rejected during image destination resolution: {0}",
                        exception);
                }
                finally
                {
                    WindowsClipboardImageDestinationInterop.ReleaseComObject(browser);
                }
            }

            return paths.ToArray();
        }
        finally
        {
            WindowsClipboardImageDestinationInterop.ReleaseComObject(shellWindows);
        }
    }

    private static nint GetBrowserWindow(object browser)
    {
        dynamic automationBrowser = browser;
        return checked((nint)Convert.ToInt64(
            automationBrowser.HWND,
            CultureInfo.InvariantCulture));
    }

    private static ActiveViewPath TryGetActiveViewPath(
        object browser,
        nint focusedWindow)
    {
        object? shellBrowserObject = null;
        object? shellViewObject = null;
        object? shellItemObject = null;
        try
        {
            var provider = (IClipboardComServiceProvider)browser;
            var serviceId =
                WindowsClipboardImageDestinationInterop.TopLevelBrowserServiceId;
            var browserInterfaceId =
                WindowsClipboardImageDestinationInterop.ShellBrowserInterfaceId;
            var result = provider.QueryService(
                ref serviceId,
                ref browserInterfaceId,
                out var shellBrowserPointer);
            if (result < 0 || shellBrowserPointer == 0)
            {
                return default;
            }

            shellBrowserObject =
                WindowsClipboardImageDestinationInterop.GetObjectAndRelease(
                    shellBrowserPointer);
            var shellBrowser = (IClipboardShellBrowser)shellBrowserObject;
            result = shellBrowser.QueryActiveShellView(out var shellViewPointer);
            if (result < 0 || shellViewPointer == 0)
            {
                return default;
            }

            shellViewObject =
                WindowsClipboardImageDestinationInterop.GetObjectAndRelease(
                    shellViewPointer);
            result = ((IClipboardShellViewWindow)shellViewObject)
                .GetWindow(out var viewWindow);
            if (result < 0 ||
                !WindowsClipboardImageDestinationInterop.IsSameOrChild(
                    viewWindow,
                    focusedWindow))
            {
                return default;
            }

            var folderView = (IClipboardFolderView)shellViewObject;
            var shellItemInterfaceId =
                WindowsClipboardImageDestinationInterop.ShellItemInterfaceId;
            result = folderView.GetFolder(
                ref shellItemInterfaceId,
                out var shellItemPointer);
            if (result < 0 || shellItemPointer == 0)
            {
                return new ActiveViewPath(viewWindow, true, null);
            }

            shellItemObject =
                WindowsClipboardImageDestinationInterop.GetObjectAndRelease(
                    shellItemPointer);
            var path =
                WindowsClipboardImageDestinationInterop.GetFileSystemFolderPath(
                    (IClipboardShellItem)shellItemObject);
            if (path is not null && !Directory.Exists(path))
            {
                path = null;
            }

            return new ActiveViewPath(viewWindow, true, path);
        }
        catch (Exception exception)
        {
            Trace.TraceWarning(
                "An active Explorer view was rejected during image destination resolution: {0}",
                exception);
            return default;
        }
        finally
        {
            WindowsClipboardImageDestinationInterop.ReleaseComObject(shellItemObject);
            WindowsClipboardImageDestinationInterop.ReleaseComObject(shellViewObject);
            WindowsClipboardImageDestinationInterop.ReleaseComObject(shellBrowserObject);
        }
    }

    private static string CopyOne(
        string sourcePath,
        string destinationDirectory,
        string requestedName)
    {
        object? operationObject = null;
        IClipboardFileOperation? operation = null;
        IClipboardShellItem? source = null;
        IClipboardShellItem? destination = null;
        var progressSink = new FileOperationProgressSink();
        var advised = false;
        var cookie = 0U;
        try
        {
            operationObject =
                WindowsClipboardImageDestinationInterop.CreateComObject(
                    WindowsClipboardImageDestinationInterop.FileOperationClassId);
            operation = (IClipboardFileOperation)operationObject;
            var flags =
                WindowsClipboardImageDestinationInterop.FileOperationSilent |
                WindowsClipboardImageDestinationInterop.FileOperationRenameOnCollision |
                WindowsClipboardImageDestinationInterop.FileOperationNoConfirmation |
                WindowsClipboardImageDestinationInterop.FileOperationNoConfirmMakeDirectory |
                WindowsClipboardImageDestinationInterop.FileOperationNoErrorUi |
                WindowsClipboardImageDestinationInterop.FileOperationDoNotCopySecurityAttributes |
                WindowsClipboardImageDestinationInterop.FileOperationEarlyFailure |
                WindowsClipboardImageDestinationInterop.FileOperationPreserveFileExtensions;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(flags));
            Marshal.ThrowExceptionForHR(operation.Advise(progressSink, out cookie));
            advised = true;
            source = WindowsClipboardImageDestinationInterop.CreateShellItem(sourcePath);
            destination = WindowsClipboardImageDestinationInterop.CreateShellItem(
                destinationDirectory);
            Marshal.ThrowExceptionForHR(operation.CopyItem(
                source,
                destination,
                requestedName,
                0));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(
                operation.GetAnyOperationsAborted(out var aborted));
            var unadviseResult = operation.Unadvise(cookie);
            advised = false;
            Marshal.ThrowExceptionForHR(unadviseResult);

            var snapshot = progressSink.Snapshot();
            if (aborted != 0 ||
                snapshot.StartOperationsCount != 1 ||
                snapshot.FinishOperationsCount != 1 ||
                snapshot.PreCopyItemCount != 1 ||
                snapshot.PostCopyItemCount != 1 ||
                snapshot.UnexpectedItemCallbackCount != 0 ||
                snapshot.FinishResult < 0 ||
                snapshot.CopyResult < 0 ||
                snapshot.NewlyCreatedPath is null ||
                snapshot.Error is not null)
            {
                throw new InvalidDataException(
                    "The Shell copy operation did not produce complete per-item success evidence.",
                    snapshot.Error);
            }

            return snapshot.NewlyCreatedPath;
        }
        finally
        {
            if (advised && operation is not null)
            {
                _ = operation.Unadvise(cookie);
            }

            WindowsClipboardImageDestinationInterop.ReleaseComObject(destination);
            WindowsClipboardImageDestinationInterop.ReleaseComObject(source);
            WindowsClipboardImageDestinationInterop.ReleaseComObject(operationObject);
            GC.KeepAlive(progressSink);
        }
    }

    private static async Task StageCanonicalPngAsync(
        ClipboardImageExportMaterialization materialization,
        string stagedPath,
        CancellationToken cancellationToken)
    {
        using var source = materialization.OpenCanonicalPngReadStream();
        await using var destination = new FileStream(
            stagedPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(128 * 1024);
        long total = 0;
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(
                        buffer.AsMemory(0, buffer.Length),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                hash.AppendData(buffer, 0, read);
                await destination.WriteAsync(
                        buffer.AsMemory(0, read),
                        cancellationToken)
                    .ConfigureAwait(false);
                total = checked(total + read);
            }

            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        var actualHash = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (total != materialization.CanonicalPngByteLength ||
            !string.Equals(
                actualHash,
                materialization.AssetHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The staged PNG does not match the verified canonical asset.");
        }
    }

    private static string HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 128 * 1024,
            FileOptions.SequentialScan);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static bool TargetIdentityMatches(ForegroundTarget target)
    {
        if (target.WindowHandle == 0)
        {
            return false;
        }

        var identity =
            WindowsClipboardImageDestinationInterop.GetWindowIdentity(
                target.WindowHandle);
        return identity.ProcessId == target.ProcessId &&
            identity.ThreadId == target.ThreadId;
    }

    private static bool PathsEqual(string first, string second) =>
        string.Equals(
            WindowsClipboardImageDestinationInterop.NormalizePath(first),
            WindowsClipboardImageDestinationInterop.NormalizePath(second),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsOwnedTemporaryRoot(string root, string temporaryBase)
    {
        var normalizedRoot =
            WindowsClipboardImageDestinationInterop.NormalizePath(root);
        var normalizedBase =
            WindowsClipboardImageDestinationInterop.NormalizePath(temporaryBase);
        return normalizedRoot.StartsWith(
                normalizedBase + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                normalizedRoot,
                normalizedBase,
                StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateFileName(string fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) ||
            fileName.Length > 180 ||
            Path.IsPathRooted(fileName) ||
            !string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal) ||
            fileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            !string.Equals(
                Path.GetExtension(fileName),
                ".png",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "The image destination file name must be a safe PNG leaf name.",
                nameof(fileName));
        }
    }

    private static void EnsureSta()
    {
        if (Thread.CurrentThread.GetApartmentState() is not ApartmentState.STA)
        {
            throw new InvalidOperationException(
                "Clipboard image destination Shell work must run on an STA thread.");
        }
    }

    private sealed class DestinationClaim : IClipboardImageDestinationClaim
    {
        private int _state;

        public DestinationClaim(
            ForegroundTarget target,
            ClipboardImageDestinationKind kind,
            string path)
        {
            Target = target;
            Kind = kind;
            Path = path;
        }

        public ClipboardImageDestinationKind Kind { get; }

        internal ForegroundTarget Target { get; }

        internal string Path { get; }

        internal bool TryBegin() =>
            Interlocked.CompareExchange(ref _state, 1, 0) == 0;

        public void Dispose() => Interlocked.CompareExchange(ref _state, 2, 0);
    }

    private sealed record ResolvedDestination(
        ClipboardImageDestinationKind Kind,
        string Path);

    private readonly record struct ActiveViewPath(
        nint ViewWindow,
        bool FocusWithinView,
        string? Path);

    private sealed record FileOperationProgressSnapshot(
        int StartOperationsCount,
        int FinishOperationsCount,
        int PreCopyItemCount,
        int PostCopyItemCount,
        int UnexpectedItemCallbackCount,
        int FinishResult,
        int CopyResult,
        string? NewlyCreatedPath,
        Exception? Error);

    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class FileOperationProgressSink :
        IClipboardFileOperationProgressSink
    {
        private const int Success = 0;
        private readonly object _gate = new();
        private int _startOperationsCount;
        private int _finishOperationsCount;
        private int _preCopyItemCount;
        private int _postCopyItemCount;
        private int _unexpectedItemCallbackCount;
        private int _finishResult;
        private int _copyResult;
        private string? _newlyCreatedPath;
        private Exception? _error;

        public int StartOperations()
        {
            lock (_gate) _startOperationsCount++;
            return Success;
        }

        public int FinishOperations(int result)
        {
            lock (_gate)
            {
                _finishOperationsCount++;
                _finishResult = result;
            }

            return Success;
        }

        public int PreCopyItem(
            uint flags,
            IClipboardShellItem? item,
            IClipboardShellItem? destinationFolder,
            string? newName)
        {
            lock (_gate) _preCopyItemCount++;
            return Success;
        }

        public int PostCopyItem(
            uint flags,
            IClipboardShellItem? item,
            IClipboardShellItem? destinationFolder,
            string? newName,
            int copyResult,
            IClipboardShellItem? newlyCreated)
        {
            string? path = null;
            Exception? error = null;
            if (newlyCreated is not null)
            {
                try
                {
                    path = WindowsClipboardImageDestinationInterop
                        .GetFileSystemItemPath(newlyCreated);
                }
                catch (Exception exception)
                {
                    error = exception;
                }
            }

            lock (_gate)
            {
                _postCopyItemCount++;
                _copyResult = copyResult;
                _newlyCreatedPath = path;
                _error ??= error;
            }

            return Success;
        }

        public int PreRenameItem(uint flags, IClipboardShellItem? item, string? newName) => Unexpected();
        public int PostRenameItem(uint flags, IClipboardShellItem? item, string? newName, int renameResult, IClipboardShellItem? newlyCreated) => Unexpected();
        public int PreMoveItem(uint flags, IClipboardShellItem? item, IClipboardShellItem? destinationFolder, string? newName) => Unexpected();
        public int PostMoveItem(uint flags, IClipboardShellItem? item, IClipboardShellItem? destinationFolder, string? newName, int moveResult, IClipboardShellItem? newlyCreated) => Unexpected();
        public int PreDeleteItem(uint flags, IClipboardShellItem? item) => Unexpected();
        public int PostDeleteItem(uint flags, IClipboardShellItem? item, int deleteResult, IClipboardShellItem? newlyCreated) => Unexpected();
        public int PreNewItem(uint flags, IClipboardShellItem? destinationFolder, string? newName) => Unexpected();
        public int PostNewItem(uint flags, IClipboardShellItem? destinationFolder, string? newName, string? templateName, uint fileAttributes, int newResult, IClipboardShellItem? newItem) => Unexpected();
        public int UpdateProgress(uint workTotal, uint workSoFar) => Success;
        public int ResetTimer() => Success;
        public int PauseTimer() => Success;
        public int ResumeTimer() => Success;

        internal FileOperationProgressSnapshot Snapshot()
        {
            lock (_gate)
            {
                return new FileOperationProgressSnapshot(
                    _startOperationsCount,
                    _finishOperationsCount,
                    _preCopyItemCount,
                    _postCopyItemCount,
                    _unexpectedItemCallbackCount,
                    _finishResult,
                    _copyResult,
                    _newlyCreatedPath,
                    _error);
            }
        }

        private int Unexpected()
        {
            lock (_gate) _unexpectedItemCallbackCount++;
            return Success;
        }
    }
}
