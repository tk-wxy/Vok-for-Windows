[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidateRange(1, [int]::MaxValue)]
    [int] $TargetProcessId,
    [Parameter(Mandatory)] [string] $ExpectedAppPath,
    [Parameter(Mandatory)] [string] $DatabasePath,
    [Parameter(Mandatory)] [string] $AppAssemblyDirectory,
    [Parameter(Mandatory)] [string] $ProbeScriptPath,
    [Parameter(Mandatory)] [string] $OutputPath,
    [Parameter(Mandatory)] [string] $StopSignalPath,
    [Parameter(Mandatory)] [string] $AllowedOutputRoot,
    [Parameter(Mandatory)] [string] $SourceRevision,
    [string] $AssetRoot,
    [ValidateRange(1, 30)] [int] $SampleIntervalSeconds = 5,
    [ValidateRange(1, 120)] [int] $MaximumMinutes = 60
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Report = [ordered]@{
    ReportVersion = 1
    Status = 'Started'
    Stage = 'Initialization'
    StartedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    SourceRevision = $SourceRevision
    TargetProcessId = $TargetProcessId
    Samples = [Collections.Generic.List[object]]::new()
}

function Resolve-AbsolutePath {
    param([Parameter(Mandatory)] [string] $Path)

    if ([string]::IsNullOrWhiteSpace($Path)) {
        throw 'A required path was empty.'
    }
    [IO.Path]::GetFullPath($Path)
}

function Test-PathWithinRoot {
    param(
        [Parameter(Mandatory)] [string] $Path,
        [Parameter(Mandatory)] [string] $Root
    )

    $absolutePath = Resolve-AbsolutePath $Path
    $absoluteRoot = (Resolve-AbsolutePath $Root).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar)
    $prefix = $absoluteRoot + [IO.Path]::DirectorySeparatorChar
    $absolutePath.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
}

function Save-Report {
    $absoluteOutput = Resolve-AbsolutePath $OutputPath
    $parent = Split-Path -Parent $absoluteOutput
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        New-Item -ItemType Directory -Path $parent -Force | Out-Null
    }

    $temporary = Join-Path $parent (
        ([IO.Path]::GetFileName($absoluteOutput)) + '.tmp-' + [Guid]::NewGuid().ToString('N'))
    try {
        $script:Report | ConvertTo-Json -Depth 14 |
            Set-Content -LiteralPath $temporary -Encoding utf8
        Move-Item -LiteralPath $temporary -Destination $absoluteOutput -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporary -PathType Leaf) {
            Remove-Item -LiteralPath $temporary -Force
        }
    }
}

function Set-ReportStage {
    param([Parameter(Mandatory)] [string] $Stage)

    $script:Report.Stage = $Stage
    Save-Report
}

function Invoke-DatabaseAudit {
    param([Parameter(Mandatory)] [string] $Name)

    $reportDirectory = Split-Path -Parent (Resolve-AbsolutePath $OutputPath)
    $auditPath = Join-Path $reportDirectory "$Name-database-audit.json"
    $arguments = @{
        Mode = 'Audit'
        DatabasePath = Resolve-AbsolutePath $DatabasePath
        AppAssemblyDirectory = Resolve-AbsolutePath $AppAssemblyDirectory
        OutputPath = $auditPath
    }
    if (-not [string]::IsNullOrWhiteSpace($AssetRoot)) {
        $arguments.AssetRoot = Resolve-AbsolutePath $AssetRoot
    }
    & (Resolve-AbsolutePath $ProbeScriptPath) @arguments
    $auditReport = Get-Content -Raw -LiteralPath $auditPath | ConvertFrom-Json
    if ($auditReport.Status -ne 'Succeeded') {
        throw "$Name database audit did not succeed."
    }
    $auditReport.Database
}

function Get-ResourceSample {
    param(
        [Parameter(Mandatory)] [Diagnostics.Process] $Process,
        [Parameter(Mandatory)] [IntPtr] $Window
    )

    $Process.Refresh()
    if ($Process.HasExited) {
        throw "Vok PID $TargetProcessId exited while recording."
    }

    $rect = [VokM3WindowProbe+RECT]::new()
    $hasRect = [VokM3WindowProbe]::GetWindowRect($Window, [ref]$rect)
    $extendedStyle = [VokM3WindowProbe]::GetWindowLongPtr($Window, -20).ToInt64()
    [ordered]@{
        TimestampUtc = [DateTimeOffset]::UtcNow.ToString('O')
        WorkingSetBytes = $Process.WorkingSet64
        PrivateBytes = $Process.PrivateMemorySize64
        HandleCount = $Process.HandleCount
        ThreadCount = $Process.Threads.Count
        UserObjectCount = [long][VokM3WindowProbe]::GetGuiResources($Process.Handle, 1)
        GdiObjectCount = [long][VokM3WindowProbe]::GetGuiResources($Process.Handle, 0)
        WindowAlive = [VokM3WindowProbe]::IsWindow($Window)
        WindowVisible = [VokM3WindowProbe]::IsWindowVisible($Window)
        WindowIsForeground = [VokM3WindowProbe]::GetForegroundWindow() -eq $Window
        WindowIsTopmost = ($extendedStyle -band 0x8) -ne 0
        WindowRect = if ($hasRect) {
            [ordered]@{
                Left = $rect.Left
                Top = $rect.Top
                Right = $rect.Right
                Bottom = $rect.Bottom
            }
        }
        else {
            $null
        }
    }
}

$absoluteOutputRoot = Resolve-AbsolutePath $AllowedOutputRoot
$absoluteOutput = Resolve-AbsolutePath $OutputPath
$absoluteStopSignal = Resolve-AbsolutePath $StopSignalPath
if (-not (Test-PathWithinRoot $absoluteOutput $absoluteOutputRoot) -or
    -not (Test-PathWithinRoot $absoluteStopSignal $absoluteOutputRoot)) {
    throw 'OutputPath and StopSignalPath must be inside AllowedOutputRoot.'
}
if (Test-Path -LiteralPath $absoluteStopSignal) {
    throw "Stop signal already exists: $absoluteStopSignal"
}

Save-Report
try {
    Set-ReportStage 'ValidateInputs'
    $absoluteAppPath = (Resolve-Path -LiteralPath $ExpectedAppPath).Path
    $absoluteProbePath = (Resolve-Path -LiteralPath $ProbeScriptPath).Path
    $absoluteDatabasePath = (Resolve-Path -LiteralPath $DatabasePath).Path
    $process = Get-Process -Id $TargetProcessId -ErrorAction Stop
    $process.Refresh()
    if ($process.HasExited) {
        throw "Vok PID $TargetProcessId has already exited."
    }
    if (-not [StringComparer]::OrdinalIgnoreCase.Equals($process.Path, $absoluteAppPath)) {
        throw "PID $TargetProcessId does not run the expected Vok executable."
    }

    if (-not ('VokM3WindowProbe' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class VokM3WindowProbe
{
    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool GetWindowRect(IntPtr window, out RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    public static extern IntPtr GetWindowLongPtr(IntPtr window, int index);

    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr process, uint flags);

    public static IntPtr FindLargestTopLevelWindow(uint targetProcessId)
    {
        IntPtr best = IntPtr.Zero;
        long bestArea = -1;
        EnumWindows((window, _) =>
        {
            uint owner;
            RECT rect;
            GetWindowThreadProcessId(window, out owner);
            if (owner != targetProcessId || !GetWindowRect(window, out rect))
            {
                return true;
            }

            long width = Math.Max(0, rect.Right - rect.Left);
            long height = Math.Max(0, rect.Bottom - rect.Top);
            long area = width * height;
            if (area > bestArea)
            {
                best = window;
                bestArea = area;
            }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
'@
    }

    $window = [VokM3WindowProbe]::FindLargestTopLevelWindow([uint32]$TargetProcessId)
    if ($window -eq [IntPtr]::Zero) {
        throw "No top-level HWND was found for Vok PID $TargetProcessId."
    }

    $script:Report.Process = [ordered]@{
        AppPath = $absoluteAppPath
        AppSha256 = (Get-FileHash -LiteralPath $absoluteAppPath -Algorithm SHA256).Hash.ToLowerInvariant()
        ProcessStartUtc = $process.StartTime.ToUniversalTime().ToString('O')
        WindowHandle = ('0x{0:X}' -f $window.ToInt64())
        ProbeScriptPath = $absoluteProbePath
        DatabasePath = $absoluteDatabasePath
    }

    Set-ReportStage 'AuditBefore'
    $script:Report.DatabaseBefore = Invoke-DatabaseAudit 'before'
    $script:Report.Samples.Add((Get-ResourceSample $process $window))
    Save-Report

    Set-ReportStage 'Recording'
    $deadline = [DateTimeOffset]::UtcNow.AddMinutes($MaximumMinutes)
    while (-not (Test-Path -LiteralPath $absoluteStopSignal -PathType Leaf)) {
        if ([DateTimeOffset]::UtcNow -ge $deadline) {
            throw "Recording exceeded the $MaximumMinutes minute limit."
        }
        Start-Sleep -Seconds $SampleIntervalSeconds
        $script:Report.Samples.Add((Get-ResourceSample $process $window))
        Save-Report
    }

    Set-ReportStage 'AuditAfter'
    $script:Report.DatabaseAfter = Invoke-DatabaseAudit 'after'
    $script:Report.Samples.Add((Get-ResourceSample $process $window))
    $firstSample = $script:Report.Samples[0]
    $lastSample = $script:Report.Samples[$script:Report.Samples.Count - 1]
    $script:Report.ResourceDelta = [ordered]@{
        WorkingSetBytes = $lastSample.WorkingSetBytes - $firstSample.WorkingSetBytes
        PrivateBytes = $lastSample.PrivateBytes - $firstSample.PrivateBytes
        HandleCount = $lastSample.HandleCount - $firstSample.HandleCount
        ThreadCount = $lastSample.ThreadCount - $firstSample.ThreadCount
        UserObjectCount = $lastSample.UserObjectCount - $firstSample.UserObjectCount
        GdiObjectCount = $lastSample.GdiObjectCount - $firstSample.GdiObjectCount
    }
    $script:Report.Status = 'Succeeded'
    $script:Report.Stage = 'Completed'
    $script:Report.CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    Save-Report
}
catch {
    $script:Report.Status = 'Failed'
    $script:Report.FailedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    $script:Report.Error = [ordered]@{
        Type = $_.Exception.GetType().FullName
        Message = $_.Exception.Message
        HResult = $_.Exception.HResult
    }
    Save-Report
    throw
}
