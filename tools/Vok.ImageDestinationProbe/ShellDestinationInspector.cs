using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Vok.Core.Workspace;

namespace Vok.ImageDestinationProbe;

internal sealed record ShellWindowCandidateReport(
    int Index,
    string WindowHandle,
    string? ActiveViewHandle,
    bool FocusWithinActiveView,
    bool ActiveViewFileSystemDirectory,
    bool AutomationFileSystemDirectory,
    bool? ActiveAndAutomationPathsAgree,
    string? ActiveViewPathHash,
    string? AutomationPathHash,
    string? ActiveViewPath,
    string? AutomationPath,
    string? ErrorCode);

internal sealed record ShellEnumerationReport(
    int ReportVersion,
    bool Success,
    string ApartmentState,
    int RegisteredWindowCount,
    IReadOnlyList<ShellWindowCandidateReport> Windows,
    string? ErrorCode);

internal sealed record DestinationInspectionReport(
    int ReportVersion,
    bool Success,
    string Classification,
    string ResolutionMethod,
    string TargetWindowHandle,
    string FocusedWindowHandle,
    uint CapturedProcessId,
    uint CapturedThreadId,
    bool TargetIdentityStillMatches,
    string TargetWindowClass,
    string FocusedWindowClass,
    int MatchingShellWindowCount,
    bool DesktopShellWindowMatch,
    bool DesktopViewClassObserved,
    bool? ExpectedPathMatches,
    string? ResolvedPathHash,
    string? ResolvedPath,
    IReadOnlyList<ShellWindowCandidateReport> Candidates,
    string? ErrorCode);

internal static class ShellDestinationInspector
{
    internal static ShellEnumerationReport Enumerate(bool includePaths)
    {
        EnsureSta();
        try
        {
            var candidates = EnumerateCore(0, includePaths, out var registeredCount);
            return new ShellEnumerationReport(
                1,
                true,
                Thread.CurrentThread.GetApartmentState().ToString(),
                registeredCount,
                candidates.Select(static candidate => candidate.Report).ToArray(),
                null);
        }
        catch (Exception exception)
        {
            return new ShellEnumerationReport(
                1,
                false,
                Thread.CurrentThread.GetApartmentState().ToString(),
                0,
                [],
                ErrorCode("Enumerate", exception));
        }
    }

    internal static DestinationInspectionReport Inspect(
        ForegroundTarget target,
        string? expectedPath,
        bool includePaths)
    {
        EnsureSta();
        var targetClass = ShellInterop.GetWindowClass(target.WindowHandle);
        var focusedClass = ShellInterop.GetWindowClass(target.FocusedWindowHandle);
        var currentIdentity = ShellInterop.GetWindowIdentity(target.WindowHandle);
        var identityMatches = target.WindowHandle != 0 &&
            currentIdentity.ProcessId == target.ProcessId &&
            currentIdentity.ThreadId == target.ThreadId;
        if (!identityMatches)
        {
            return Result(
                target,
                success: true,
                classification: "StaleTarget",
                resolutionMethod: "None",
                targetClass,
                focusedClass,
                identityMatches,
                matchingCount: 0,
                desktopShellWindowMatch: false,
                desktopViewClassObserved: false,
                expectedPathMatches: null,
                resolvedPath: null,
                candidates: [],
                includePaths,
                errorCode: null);
        }

        List<Candidate> allCandidates;
        try
        {
            allCandidates = EnumerateCore(
                target.FocusedWindowHandle,
                includePaths,
                out _);
        }
        catch (Exception exception)
        {
            return Result(
                target,
                success: false,
                classification: "InspectionFailed",
                resolutionMethod: "None",
                targetClass,
                focusedClass,
                identityMatches,
                matchingCount: 0,
                desktopShellWindowMatch: false,
                desktopViewClassObserved: false,
                expectedPathMatches: null,
                resolvedPath: null,
                candidates: [],
                includePaths,
                ErrorCode("ShellWindows", exception));
        }

        var matching = allCandidates
            .Where(candidate => candidate.WindowHandle == target.WindowHandle)
            .ToArray();
        var selected = SelectExplorerPath(matching);
        if (selected.Status is ExplorerSelectionStatus.Resolved)
        {
            return Result(
                target,
                success: true,
                classification: "FileSystemFolder",
                resolutionMethod: "ActiveShellView",
                targetClass,
                focusedClass,
                identityMatches,
                matching.Length,
                desktopShellWindowMatch: false,
                desktopViewClassObserved: false,
                ExpectedMatches(selected.Path, expectedPath),
                selected.Path,
                matching.Select(static candidate => candidate.Report).ToArray(),
                includePaths,
                errorCode: null);
        }

        if (selected.Status is ExplorerSelectionStatus.Ambiguous)
        {
            return Result(
                target,
                success: true,
                classification: "Ambiguous",
                resolutionMethod: "ActiveShellView",
                targetClass,
                focusedClass,
                identityMatches,
                matching.Length,
                desktopShellWindowMatch: false,
                desktopViewClassObserved: false,
                expectedPathMatches: null,
                resolvedPath: null,
                matching.Select(static candidate => candidate.Report).ToArray(),
                includePaths,
                errorCode: null);
        }

        var shellWindowMatch = target.WindowHandle != 0 &&
            target.WindowHandle == ShellInterop.ShellWindow;
        var desktopViewObserved =
            string.Equals(targetClass, "SHELLDLL_DefView", StringComparison.Ordinal) ||
            ShellInterop.ContainsDescendantClass(target.WindowHandle, "SHELLDLL_DefView");
        var desktopClass = targetClass is "Progman" or "WorkerW";
        if (shellWindowMatch || (desktopClass && desktopViewObserved))
        {
            try
            {
                var desktopPath = ShellInterop.GetDesktopKnownFolderPath();
                if (Directory.Exists(desktopPath))
                {
                    return Result(
                        target,
                        success: true,
                        classification: "Desktop",
                        resolutionMethod: "KnownFolderDesktop",
                        targetClass,
                        focusedClass,
                        identityMatches,
                        matching.Length,
                        shellWindowMatch,
                        desktopViewObserved,
                        ExpectedMatches(desktopPath, expectedPath),
                        desktopPath,
                        matching.Select(static candidate => candidate.Report).ToArray(),
                        includePaths,
                        errorCode: null);
                }
            }
            catch (Exception exception)
            {
                return Result(
                    target,
                    success: false,
                    classification: "InspectionFailed",
                    resolutionMethod: "KnownFolderDesktop",
                    targetClass,
                    focusedClass,
                    identityMatches,
                    matching.Length,
                    shellWindowMatch,
                    desktopViewObserved,
                    expectedPathMatches: null,
                    resolvedPath: null,
                    matching.Select(static candidate => candidate.Report).ToArray(),
                    includePaths,
                    ErrorCode("Desktop", exception));
            }
        }

        var automationPaths = matching
            .Select(static candidate => candidate.AutomationPath)
            .Where(static path => path is not null)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (automationPaths.Length == 1)
        {
            return Result(
                target,
                success: true,
                classification: "ExplorerAutomationOnly",
                resolutionMethod: "AutomationDiagnosticOnly",
                targetClass,
                focusedClass,
                identityMatches,
                matching.Length,
                shellWindowMatch,
                desktopViewObserved,
                ExpectedMatches(automationPaths[0], expectedPath),
                automationPaths[0],
                matching.Select(static candidate => candidate.Report).ToArray(),
                includePaths,
                errorCode: null);
        }

        return Result(
            target,
            success: true,
            classification: automationPaths.Length > 1 ? "Ambiguous" : "Unsupported",
            resolutionMethod: "None",
            targetClass,
            focusedClass,
            identityMatches,
            matching.Length,
            shellWindowMatch,
            desktopViewObserved,
            expectedPathMatches: null,
            resolvedPath: null,
            matching.Select(static candidate => candidate.Report).ToArray(),
            includePaths,
            errorCode: null);
    }

    private static List<Candidate> EnumerateCore(
        nint focusedWindow,
        bool includePaths,
        out int registeredCount)
    {
        object? shellWindows = null;
        try
        {
            shellWindows = ShellInterop.CreateComObject(ShellInterop.ShellWindowsClassId);
            dynamic windows = shellWindows;
            registeredCount = Convert.ToInt32(windows.Count, CultureInfo.InvariantCulture);
            var candidates = new List<Candidate>(registeredCount);
            for (var index = 0; index < registeredCount; index++)
            {
                object? browser = null;
                try
                {
                    browser = windows.Item(index);
                    if (browser is not null)
                    {
                        candidates.Add(InspectBrowser(index, browser, focusedWindow, includePaths));
                    }
                }
                catch (Exception exception)
                {
                    candidates.Add(Candidate.Failed(index, ErrorCode("Item", exception)));
                }
                finally
                {
                    ShellInterop.ReleaseComObject(browser);
                }
            }

            return candidates;
        }
        finally
        {
            ShellInterop.ReleaseComObject(shellWindows);
        }
    }

    private static Candidate InspectBrowser(
        int index,
        object browser,
        nint focusedWindow,
        bool includePaths)
    {
        nint browserWindow;
        try
        {
            dynamic automationBrowser = browser;
            browserWindow = checked((nint)Convert.ToInt64(
                automationBrowser.HWND,
                CultureInfo.InvariantCulture));
        }
        catch (Exception exception)
        {
            return Candidate.Failed(index, ErrorCode("BrowserHwnd", exception));
        }

        var active = TryGetActiveView(browser, focusedWindow);
        var automation = TryGetAutomationPath(browser);
        bool? pathsAgree = active.Path is null || automation.Path is null
            ? null
            : PathEquals(active.Path, automation.Path);
        var error = JoinErrors(active.ErrorCode, automation.ErrorCode);
        return new Candidate(
            browserWindow,
            active.Path,
            automation.Path,
            new ShellWindowCandidateReport(
                index,
                Handle(browserWindow),
                active.ViewWindow == 0 ? null : Handle(active.ViewWindow),
                active.FocusWithinView,
                active.Path is not null,
                automation.Path is not null,
                pathsAgree,
                PathHash(active.Path),
                PathHash(automation.Path),
                includePaths ? active.Path : null,
                includePaths ? automation.Path : null,
                error));
    }

    private static ActiveViewResult TryGetActiveView(object browser, nint focusedWindow)
    {
        object? shellBrowserObject = null;
        object? shellViewObject = null;
        object? shellItemObject = null;
        var stage = "ProviderCast";
        string? diagnosticError = null;
        try
        {
            var provider = (IComServiceProvider)browser;
            stage = "QueryService";
            var serviceId = ShellInterop.TopLevelBrowserServiceId;
            var browserInterfaceId = ShellInterop.ShellBrowserInterfaceId;
            var result = provider.QueryService(
                ref serviceId,
                ref browserInterfaceId,
                out var shellBrowserPointer);
            if (result < 0 || shellBrowserPointer == 0)
            {
                return ActiveViewResult.Failed($"QueryService:0x{result:X8}");
            }

            shellBrowserObject = ShellInterop.GetObjectAndRelease(shellBrowserPointer);
            stage = "ShellBrowserCast";
            var shellBrowser = (IShellBrowser)shellBrowserObject;
            stage = "QueryActiveShellView";
            result = shellBrowser.QueryActiveShellView(out var shellViewPointer);
            if (result < 0 || shellViewPointer == 0)
            {
                return ActiveViewResult.Failed($"QueryActiveShellView:0x{result:X8}");
            }

            shellViewObject = ShellInterop.GetObjectAndRelease(shellViewPointer);
            stage = "OleWindowCast";
            var viewWindow = nint.Zero;
            try
            {
                var viewWindowResult = ((IShellViewWindow)shellViewObject)
                    .GetWindow(out viewWindow);
                if (viewWindowResult < 0)
                {
                    diagnosticError = $"GetViewWindow:0x{viewWindowResult:X8}";
                    viewWindow = 0;
                }
            }
            catch (Exception exception)
            {
                diagnosticError = ErrorCode(stage, exception);
            }

            stage = "FolderViewCast";
            var folderView = (IFolderView)shellViewObject;
            var shellItemInterfaceId = ShellInterop.ShellItemInterfaceId;
            stage = "GetFolder";
            result = folderView.GetFolder(ref shellItemInterfaceId, out var shellItemPointer);
            if (result < 0 || shellItemPointer == 0)
            {
                return new ActiveViewResult(
                    viewWindow,
                    ShellInterop.IsSameOrChild(viewWindow, focusedWindow),
                    null,
                    $"GetFolder:0x{result:X8}");
            }

            shellItemObject = ShellInterop.GetObjectAndRelease(shellItemPointer);
            stage = "ShellItemCast";
            var path = ShellInterop.GetFileSystemPath((IShellItem)shellItemObject);
            if (path is not null && !Directory.Exists(path))
            {
                path = null;
            }

            return new ActiveViewResult(
                viewWindow,
                ShellInterop.IsSameOrChild(viewWindow, focusedWindow),
                path,
                diagnosticError);
        }
        catch (Exception exception)
        {
            return ActiveViewResult.Failed(ErrorCode(stage, exception));
        }
        finally
        {
            ShellInterop.ReleaseComObject(shellItemObject);
            ShellInterop.ReleaseComObject(shellViewObject);
            ShellInterop.ReleaseComObject(shellBrowserObject);
        }
    }

    private static AutomationResult TryGetAutomationPath(object browser)
    {
        object? document = null;
        object? folder = null;
        object? item = null;
        try
        {
            dynamic automationBrowser = browser;
            document = automationBrowser.Document;
            if (document is null)
            {
                return AutomationResult.Failed("Document:null");
            }

            dynamic automationDocument = document;
            folder = automationDocument.Folder;
            if (folder is null)
            {
                return AutomationResult.Failed("Folder:null");
            }

            dynamic automationFolder = folder;
            item = automationFolder.Self;
            if (item is null)
            {
                return AutomationResult.Failed("Self:null");
            }

            dynamic automationItem = item;
            var rawPath = Convert.ToString(automationItem.Path, CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(rawPath) || !Directory.Exists(rawPath))
            {
                return new AutomationResult(null, null);
            }

            return new AutomationResult(ProbeOptions.NormalizePath(rawPath), null);
        }
        catch (Exception exception)
        {
            return AutomationResult.Failed(ErrorCode("Automation", exception));
        }
        finally
        {
            ShellInterop.ReleaseComObject(item);
            ShellInterop.ReleaseComObject(folder);
            ShellInterop.ReleaseComObject(document);
        }
    }

    private static ExplorerSelection SelectExplorerPath(IReadOnlyList<Candidate> candidates)
    {
        var withPath = candidates.Where(static candidate => candidate.ActivePath is not null).ToArray();
        var focused = withPath.Where(static candidate => candidate.Report.FocusWithinActiveView).ToArray();
        var pool = focused.Length > 0 ? focused : withPath;
        var paths = pool
            .Select(static candidate => candidate.ActivePath!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return paths.Length switch
        {
            0 => new ExplorerSelection(ExplorerSelectionStatus.None, null),
            1 => new ExplorerSelection(ExplorerSelectionStatus.Resolved, paths[0]),
            _ => new ExplorerSelection(ExplorerSelectionStatus.Ambiguous, null),
        };
    }

    private static DestinationInspectionReport Result(
        ForegroundTarget target,
        bool success,
        string classification,
        string resolutionMethod,
        string targetClass,
        string focusedClass,
        bool identityMatches,
        int matchingCount,
        bool desktopShellWindowMatch,
        bool desktopViewClassObserved,
        bool? expectedPathMatches,
        string? resolvedPath,
        IReadOnlyList<ShellWindowCandidateReport> candidates,
        bool includePaths,
        string? errorCode) =>
        new(
            1,
            success,
            classification,
            resolutionMethod,
            Handle(target.WindowHandle),
            Handle(target.FocusedWindowHandle),
            target.ProcessId,
            target.ThreadId,
            identityMatches,
            targetClass,
            focusedClass,
            matchingCount,
            desktopShellWindowMatch,
            desktopViewClassObserved,
            expectedPathMatches,
            PathHash(resolvedPath),
            includePaths ? resolvedPath : null,
            candidates,
            errorCode);

    private static bool? ExpectedMatches(string? resolved, string? expected) =>
        expected is null || resolved is null ? null : PathEquals(resolved, expected);

    private static bool PathEquals(string first, string second) =>
        string.Equals(
            ProbeOptions.NormalizePath(first),
            ProbeOptions.NormalizePath(second),
            StringComparison.OrdinalIgnoreCase);

    private static string? PathHash(string? path)
    {
        if (path is null)
        {
            return null;
        }

        var normalized = ProbeOptions.NormalizePath(path).ToUpperInvariant();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private static string Handle(nint value) =>
        string.Create(CultureInfo.InvariantCulture, $"0x{(nuint)value:X}");

    private static string ErrorCode(string stage, Exception exception) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{stage}:{exception.GetType().Name}:0x{exception.HResult:X8}");

    private static string? JoinErrors(string? first, string? second) =>
        first is null ? second : second is null ? first : $"{first}|{second}";

    private static void EnsureSta()
    {
        if (Thread.CurrentThread.GetApartmentState() is not ApartmentState.STA)
        {
            throw new InvalidOperationException("Shell inspection must run on an STA thread.");
        }
    }

    private sealed record Candidate(
        nint WindowHandle,
        string? ActivePath,
        string? AutomationPath,
        ShellWindowCandidateReport Report)
    {
        public static Candidate Failed(int index, string errorCode) => new(
            0,
            null,
            null,
            new ShellWindowCandidateReport(
                index,
                Handle(0),
                null,
                false,
                false,
                false,
                null,
                null,
                null,
                null,
                null,
                errorCode));
    }

    private sealed record ActiveViewResult(
        nint ViewWindow,
        bool FocusWithinView,
        string? Path,
        string? ErrorCode)
    {
        public static ActiveViewResult Failed(string errorCode) =>
            new(0, false, null, errorCode);
    }

    private sealed record AutomationResult(string? Path, string? ErrorCode)
    {
        public static AutomationResult Failed(string errorCode) => new(null, errorCode);
    }

    private enum ExplorerSelectionStatus
    {
        None,
        Resolved,
        Ambiguous,
    }

    private sealed record ExplorerSelection(
        ExplorerSelectionStatus Status,
        string? Path);
}
