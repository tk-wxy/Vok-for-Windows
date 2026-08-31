using System.Runtime.InteropServices;
using Vok.Infrastructure.Windows.Launcher;

namespace Vok.ImageDestinationProbe;

internal sealed record FileOperationProbeReport(
    int ReportVersion,
    bool Success,
    string ApartmentState,
    bool PreClaimCancellationHadNoSideEffect,
    bool DesktopKnownFolderAvailable,
    string RequestedName,
    string? FirstCreatedName,
    string? SecondCreatedName,
    bool FirstNameMatched,
    bool CollisionRenamed,
    bool ExtensionPreserved,
    bool SourcePreserved,
    bool CreatedBytesMatched,
    bool OperationsWereNotAborted,
    bool ProgressLifecycleBalanced,
    bool PostCopyItemObserved,
    bool PostCopyItemSucceeded,
    bool PostCopyItemCreatedItemAvailable,
    bool PostCopyItemPathsMatched,
    bool ProgressSinksUnadvised,
    bool TemporaryFilesCleaned,
    string? ErrorCode);

internal static class ShellFileOperationProbe
{
    private const string RequestedName = "Vok 图片 2026-08-31 142345.png";
    private static readonly byte[] SourceBytes = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M/wHwAF/gL+R8H0GQAAAABJRU5ErkJggg==");

    internal static async Task<FileOperationProbeReport> RunAsync(
        WindowsShellStaExecutor executor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(executor);
        var cancellationSafe = await VerifyPreClaimCancellationAsync(
            executor,
            cancellationToken).ConfigureAwait(false);
        return await executor.ExecuteAsync(
                () => RunCore(cancellationSafe),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task<bool> VerifyPreClaimCancellationAsync(
        WindowsShellStaExecutor executor,
        CancellationToken cancellationToken)
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var blocker = executor.ExecuteAsync(
            () =>
            {
                entered.Set();
                release.Wait(cancellationToken);
                return true;
            },
            cancellationToken);
        if (!entered.Wait(TimeSpan.FromSeconds(5), cancellationToken))
        {
            release.Set();
            throw new TimeoutException("The Shell STA cancellation gate did not start.");
        }

        using var cancellation = new CancellationTokenSource();
        var executionCount = 0;
        var cancelled = executor.ExecuteAsync(
            () =>
            {
                executionCount++;
                return true;
            },
            cancellation.Token);
        cancellation.Cancel();
        try
        {
            await cancelled.ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            return executionCount == 0;
        }
        finally
        {
            release.Set();
            _ = await blocker.ConfigureAwait(false);
        }
    }

    private static FileOperationProbeReport RunCore(bool cancellationSafe)
    {
        var apartment = Thread.CurrentThread.GetApartmentState();
        var desktopAvailable = false;
        string? desktopErrorCode = null;
        try
        {
            desktopAvailable = Directory.Exists(ShellInterop.GetDesktopKnownFolderPath());
        }
        catch (Exception exception)
        {
            desktopErrorCode = $"Desktop:{ErrorCode(exception)}";
        }

        var probeBase = ProbeOptions.NormalizePath(Path.Combine(
            Path.GetTempPath(),
            "Vok.ImageDestinationProbe"));
        var root = Path.Combine(probeBase, Guid.NewGuid().ToString("N"));
        var sourceDirectory = Path.Combine(root, "source");
        var destinationDirectory = Path.Combine(root, "destination");
        var sourcePath = Path.Combine(sourceDirectory, "canonical.png");
        var cleaned = false;
        FileOperationProbeReport report;
        try
        {
            Directory.CreateDirectory(sourceDirectory);
            Directory.CreateDirectory(destinationDirectory);
            File.WriteAllBytes(sourcePath, SourceBytes);

            var firstOperation = CopyOne(sourcePath, destinationDirectory, RequestedName);
            var afterFirst = Directory.GetFiles(destinationDirectory);
            var firstPath = AssertSingleNewFile(afterFirst, []);
            var secondOperation = CopyOne(sourcePath, destinationDirectory, RequestedName);
            var afterSecond = Directory.GetFiles(destinationDirectory);
            var secondPath = AssertSingleNewFile(afterSecond, afterFirst);

            var firstName = Path.GetFileName(firstPath);
            var secondName = Path.GetFileName(secondPath);
            var firstNameMatched = string.Equals(
                firstName,
                RequestedName,
                StringComparison.OrdinalIgnoreCase);
            var collisionRenamed = !string.Equals(
                secondName,
                RequestedName,
                StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(secondName, firstName, StringComparison.OrdinalIgnoreCase);
            var extensionPreserved = string.Equals(
                    Path.GetExtension(firstName),
                    ".png",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    Path.GetExtension(secondName),
                    ".png",
                    StringComparison.OrdinalIgnoreCase);
            var sourcePreserved = File.Exists(sourcePath) &&
                File.ReadAllBytes(sourcePath).SequenceEqual(SourceBytes);
            var bytesMatched = File.ReadAllBytes(firstPath).SequenceEqual(SourceBytes) &&
                File.ReadAllBytes(secondPath).SequenceEqual(SourceBytes);
            var notAborted = !firstOperation.Aborted && !secondOperation.Aborted;
            var progressLifecycleBalanced =
                firstOperation.ProgressLifecycleBalanced &&
                secondOperation.ProgressLifecycleBalanced;
            var postCopyItemObserved =
                firstOperation.PostCopyItemObserved &&
                secondOperation.PostCopyItemObserved;
            var postCopyItemSucceeded =
                firstOperation.PostCopyItemSucceeded &&
                secondOperation.PostCopyItemSucceeded;
            var postCopyItemCreatedItemAvailable =
                firstOperation.PostCopyItemCreatedItemAvailable &&
                secondOperation.PostCopyItemCreatedItemAvailable;
            var postCopyItemPathsMatched =
                PathsEqual(firstOperation.CreatedPath, firstPath) &&
                PathsEqual(secondOperation.CreatedPath, secondPath);
            var progressSinksUnadvised =
                firstOperation.ProgressSinkUnadvised &&
                secondOperation.ProgressSinkUnadvised;
            var progressError = JoinErrors(
                firstOperation.ErrorCode,
                secondOperation.ErrorCode);
            if (!postCopyItemPathsMatched)
            {
                progressError = JoinErrors(progressError, "PostCopyItemPathMismatch");
            }

            var success =
                apartment is ApartmentState.STA &&
                cancellationSafe &&
                desktopAvailable &&
                firstNameMatched &&
                collisionRenamed &&
                extensionPreserved &&
                sourcePreserved &&
                bytesMatched &&
                notAborted &&
                progressLifecycleBalanced &&
                postCopyItemObserved &&
                postCopyItemSucceeded &&
                postCopyItemCreatedItemAvailable &&
                postCopyItemPathsMatched &&
                progressSinksUnadvised &&
                afterSecond.Length == 2;

            report = new FileOperationProbeReport(
                2,
                success,
                apartment.ToString(),
                cancellationSafe,
                desktopAvailable,
                RequestedName,
                firstName,
                secondName,
                firstNameMatched,
                collisionRenamed,
                extensionPreserved,
                sourcePreserved,
                bytesMatched,
                notAborted,
                progressLifecycleBalanced,
                postCopyItemObserved,
                postCopyItemSucceeded,
                postCopyItemCreatedItemAvailable,
                postCopyItemPathsMatched,
                progressSinksUnadvised,
                TemporaryFilesCleaned: false,
                ErrorCode: JoinErrors(desktopErrorCode, progressError));
        }
        catch (Exception exception)
        {
            report = new FileOperationProbeReport(
                2,
                false,
                apartment.ToString(),
                cancellationSafe,
                desktopAvailable,
                RequestedName,
                null,
                null,
                false,
                false,
                false,
                File.Exists(sourcePath),
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                TemporaryFilesCleaned: false,
                JoinErrors(desktopErrorCode, ErrorCode(exception)));
        }
        finally
        {
            if (IsOwnedProbeDirectory(root, probeBase) && Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            cleaned = !Directory.Exists(root);
        }

        return report with
        {
            Success = report.Success && cleaned,
            TemporaryFilesCleaned = cleaned,
        };
    }

    private static CopyOperationEvidence CopyOne(
        string sourcePath,
        string destinationDirectory,
        string requestedName)
    {
        object? operationObject = null;
        IFileOperation? operation = null;
        IShellItem? source = null;
        IShellItem? destination = null;
        var progressSink = new FileOperationProgressSink();
        var advised = false;
        var cookie = 0U;
        try
        {
            operationObject = ShellInterop.CreateComObject(ShellInterop.FileOperationClassId);
            operation = (IFileOperation)operationObject;
            var flags =
                ShellInterop.FileOperationSilent |
                ShellInterop.FileOperationRenameOnCollision |
                ShellInterop.FileOperationNoConfirmation |
                ShellInterop.FileOperationNoConfirmMakeDirectory |
                ShellInterop.FileOperationNoErrorUi |
                ShellInterop.FileOperationDoNotCopySecurityAttributes |
                ShellInterop.FileOperationEarlyFailure |
                ShellInterop.FileOperationPreserveFileExtensions;
            Marshal.ThrowExceptionForHR(operation.SetOperationFlags(flags));
            Marshal.ThrowExceptionForHR(operation.Advise(progressSink, out cookie));
            advised = true;
            source = ShellInterop.CreateShellItem(sourcePath);
            destination = ShellInterop.CreateShellItem(destinationDirectory);
            Marshal.ThrowExceptionForHR(operation.CopyItem(
                source,
                destination,
                requestedName,
                0));
            Marshal.ThrowExceptionForHR(operation.PerformOperations());
            Marshal.ThrowExceptionForHR(operation.GetAnyOperationsAborted(out var aborted));
            var unadviseResult = operation.Unadvise(cookie);
            advised = false;
            Marshal.ThrowExceptionForHR(unadviseResult);

            var snapshot = progressSink.Snapshot();
            var lifecycleBalanced =
                snapshot.StartOperationsCount == 1 &&
                snapshot.FinishOperationsCount == 1 &&
                snapshot.PreCopyItemCount == 1 &&
                snapshot.PostCopyItemCount == 1 &&
                snapshot.UnexpectedItemCallbackCount == 0 &&
                snapshot.FinishResult >= 0;
            var postCopyObserved = snapshot.PostCopyItemCount == 1;
            var postCopySucceeded =
                postCopyObserved && snapshot.CopyResult >= 0;
            var createdItemAvailable =
                postCopyObserved && snapshot.NewlyCreatedPath is not null;
            var evidenceError = BuildProgressError(snapshot);
            return new CopyOperationEvidence(
                aborted != 0,
                lifecycleBalanced,
                postCopyObserved,
                postCopySucceeded,
                createdItemAvailable,
                snapshot.NewlyCreatedPath,
                ProgressSinkUnadvised: true,
                evidenceError);
        }
        finally
        {
            if (advised && operation is not null)
            {
                _ = operation.Unadvise(cookie);
            }

            ShellInterop.ReleaseComObject(destination);
            ShellInterop.ReleaseComObject(source);
            ShellInterop.ReleaseComObject(operationObject);
            GC.KeepAlive(progressSink);
        }
    }

    private static string? BuildProgressError(FileOperationProgressSnapshot snapshot)
    {
        var error = snapshot.ErrorCode;
        if (snapshot.StartOperationsCount != 1)
        {
            error = JoinErrors(
                error,
                $"StartOperationsCount:{snapshot.StartOperationsCount}");
        }

        if (snapshot.FinishOperationsCount != 1)
        {
            error = JoinErrors(
                error,
                $"FinishOperationsCount:{snapshot.FinishOperationsCount}");
        }
        else if (snapshot.FinishResult < 0)
        {
            error = JoinErrors(error, $"FinishOperations:0x{snapshot.FinishResult:X8}");
        }

        if (snapshot.PreCopyItemCount != 1)
        {
            error = JoinErrors(
                error,
                $"PreCopyItemCount:{snapshot.PreCopyItemCount}");
        }

        if (snapshot.PostCopyItemCount != 1)
        {
            error = JoinErrors(
                error,
                $"PostCopyItemCount:{snapshot.PostCopyItemCount}");
        }
        else if (snapshot.CopyResult < 0)
        {
            error = JoinErrors(error, $"PostCopyItem:0x{snapshot.CopyResult:X8}");
        }

        if (snapshot.PostCopyItemCount == 1 && snapshot.NewlyCreatedPath is null)
        {
            error = JoinErrors(error, "PostCopyItemCreatedItemUnavailable");
        }

        if (snapshot.UnexpectedItemCallbackCount != 0)
        {
            error = JoinErrors(
                error,
                $"UnexpectedItemCallbackCount:{snapshot.UnexpectedItemCallbackCount}");
        }

        return error;
    }

    private static bool PathsEqual(string? first, string? second) =>
        first is not null &&
        second is not null &&
        string.Equals(
            ProbeOptions.NormalizePath(first),
            ProbeOptions.NormalizePath(second),
            StringComparison.OrdinalIgnoreCase);

    private static string AssertSingleNewFile(
        IReadOnlyList<string> current,
        IReadOnlyList<string> previous)
    {
        var previousSet = previous.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = current.Where(path => !previousSet.Contains(path)).ToArray();
        return added.Length == 1
            ? added[0]
            : throw new InvalidOperationException("Shell copy did not create exactly one new file.");
    }

    private static bool IsOwnedProbeDirectory(string root, string probeBase)
    {
        var normalizedRoot = ProbeOptions.NormalizePath(root);
        var normalizedBase = ProbeOptions.NormalizePath(probeBase);
        return normalizedRoot.StartsWith(
                normalizedBase + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedRoot, normalizedBase, StringComparison.OrdinalIgnoreCase);
    }

    private static string ErrorCode(Exception exception) =>
        $"{exception.GetType().Name}:0x{exception.HResult:X8}";

    private static string? JoinErrors(string? first, string? second) =>
        first is null ? second : second is null ? first : $"{first}|{second}";

    private sealed record CopyOperationEvidence(
        bool Aborted,
        bool ProgressLifecycleBalanced,
        bool PostCopyItemObserved,
        bool PostCopyItemSucceeded,
        bool PostCopyItemCreatedItemAvailable,
        string? CreatedPath,
        bool ProgressSinkUnadvised,
        string? ErrorCode);
}

internal sealed record FileOperationProgressSnapshot(
    int StartOperationsCount,
    int FinishOperationsCount,
    int PreCopyItemCount,
    int PostCopyItemCount,
    int UnexpectedItemCallbackCount,
    int FinishResult,
    int CopyResult,
    string? NewlyCreatedPath,
    string? ErrorCode);

[ComVisible(true)]
[ClassInterface(ClassInterfaceType.None)]
internal sealed class FileOperationProgressSink : IFileOperationProgressSink
{
    private const int Success = 0;
    private readonly object gate = new();
    private int startOperationsCount;
    private int finishOperationsCount;
    private int preCopyItemCount;
    private int postCopyItemCount;
    private int unexpectedItemCallbackCount;
    private int finishResult;
    private int copyResult;
    private string? newlyCreatedPath;
    private string? errorCode;

    public int StartOperations()
    {
        lock (gate)
        {
            startOperationsCount++;
        }

        return Success;
    }

    public int FinishOperations(int result)
    {
        lock (gate)
        {
            finishOperationsCount++;
            finishResult = result;
        }

        return Success;
    }

    public int PreRenameItem(uint flags, IShellItem? item, string? newName) =>
        RecordUnexpectedItemCallback();

    public int PostRenameItem(
        uint flags,
        IShellItem? item,
        string? newName,
        int renameResult,
        IShellItem? newlyCreated) =>
        RecordUnexpectedItemCallback();

    public int PreMoveItem(
        uint flags,
        IShellItem? item,
        IShellItem? destinationFolder,
        string? newName) =>
        RecordUnexpectedItemCallback();

    public int PostMoveItem(
        uint flags,
        IShellItem? item,
        IShellItem? destinationFolder,
        string? newName,
        int moveResult,
        IShellItem? newlyCreated) =>
        RecordUnexpectedItemCallback();

    public int PreCopyItem(
        uint flags,
        IShellItem? item,
        IShellItem? destinationFolder,
        string? newName)
    {
        lock (gate)
        {
            preCopyItemCount++;
        }

        return Success;
    }

    public int PostCopyItem(
        uint flags,
        IShellItem? item,
        IShellItem? destinationFolder,
        string? newName,
        int result,
        IShellItem? newlyCreated)
    {
        string? createdPath = null;
        string? pathError = null;
        if (newlyCreated is not null)
        {
            try
            {
                createdPath = ShellInterop.GetFileSystemItemPath(newlyCreated);
            }
            catch (Exception exception)
            {
                pathError =
                    $"PostCopyItemPath:{exception.GetType().Name}:0x{exception.HResult:X8}";
            }
        }

        lock (gate)
        {
            postCopyItemCount++;
            copyResult = result;
            newlyCreatedPath = createdPath;
            errorCode ??= pathError;
        }

        return Success;
    }

    public int PreDeleteItem(uint flags, IShellItem? item) =>
        RecordUnexpectedItemCallback();

    public int PostDeleteItem(
        uint flags,
        IShellItem? item,
        int deleteResult,
        IShellItem? newlyCreated) =>
        RecordUnexpectedItemCallback();

    public int PreNewItem(
        uint flags,
        IShellItem? destinationFolder,
        string? newName) =>
        RecordUnexpectedItemCallback();

    public int PostNewItem(
        uint flags,
        IShellItem? destinationFolder,
        string? newName,
        string? templateName,
        uint fileAttributes,
        int newResult,
        IShellItem? newItem) =>
        RecordUnexpectedItemCallback();

    public int UpdateProgress(uint workTotal, uint workSoFar) => Success;

    public int ResetTimer() => Success;

    public int PauseTimer() => Success;

    public int ResumeTimer() => Success;

    internal FileOperationProgressSnapshot Snapshot()
    {
        lock (gate)
        {
            return new FileOperationProgressSnapshot(
                startOperationsCount,
                finishOperationsCount,
                preCopyItemCount,
                postCopyItemCount,
                unexpectedItemCallbackCount,
                finishResult,
                copyResult,
                newlyCreatedPath,
                errorCode);
        }
    }

    private int RecordUnexpectedItemCallback()
    {
        lock (gate)
        {
            unexpectedItemCallbackCount++;
        }

        return Success;
    }
}
