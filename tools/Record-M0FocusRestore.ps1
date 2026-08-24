[CmdletBinding()]
param(
    [ValidateRange(10, 600)] [int] $TimeoutSeconds = 180,
    [ValidateRange(1, 60)] [int] $AttachTimeoutSeconds = 15,
    [ValidateRange(100, 5000)] [int] $PostRestoreObservationMilliseconds = 1000,
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$appPath = Join-Path $repositoryRoot 'src\Vok.App\bin\Release\net10.0-windows\Vok.exe'
if (-not $OutputPath) {
    $stamp = [DateTime]::Now.ToString('yyyyMMdd-HHmmss-fff')
    $OutputPath = Join-Path $repositoryRoot "artifacts\m0\focus-restore-physical-$stamp-$PID.json"
}
elseif (-not [IO.Path]::IsPathRooted($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot $OutputPath
}

$recorderState = [pscustomobject][ordered]@{
    Schema = 2
    Status = 'Starting'
    UpdatedAt = [DateTimeOffset]::Now
    AppPath = $appPath
    ProcessId = $null
    DiagnosticWindow = $null
    PostRestoreObservationMilliseconds = $PostRestoreObservationMilliseconds
    Error = $null
    Note = 'Read-only physical recorder; classifications are evidence for review, not an implemented fix.'
    Records = @()
}

function Save-RecorderState {
    $recorderState.UpdatedAt = [DateTimeOffset]::Now
    $parent = Split-Path -Parent $OutputPath
    if ($parent) { [void][IO.Directory]::CreateDirectory($parent) }
    [IO.File]::WriteAllText(
        $OutputPath,
        ($recorderState | ConvertTo-Json -Depth 9),
        [Text.UTF8Encoding]::new($false))
}

Save-RecorderState
Write-Host "Diagnostic file created immediately: $OutputPath"

trap {
    $recorderState.Status = 'Failed'
    $recorderState.Error = $_.Exception.ToString()
    Save-RecorderState
    Write-Host "Recorder failed; diagnostic state was saved: $OutputPath" -ForegroundColor Red
    Write-Host $_.Exception.Message -ForegroundColor Red
    exit 1
}

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class VokFocusRestoreRecorderNative
{
    private const string Prefix = "Vok.M0.Diagnostics.";

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public RECT CaretRect;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsChild(IntPtr parent, IntPtr child);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);

    public static IntPtr FindLargestWindow(uint processId)
    {
        IntPtr largest = IntPtr.Zero;
        long largestArea = -1;
        EnumWindows((window, _) =>
        {
            uint owner;
            RECT rect;
            GetWindowThreadProcessId(window, out owner);
            if (owner != processId || !GetWindowRect(window, out rect)) return true;
            long area = Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top);
            if (area > largestArea) { largestArea = area; largest = window; }
            return true;
        }, IntPtr.Zero);
        return largest;
    }

    public static long GetRawDiagnostic(IntPtr window, string suffix)
    {
        return GetProp(window, Prefix + suffix).ToInt64();
    }

    public static IntPtr GetFocus(uint threadId)
    {
        var info = new GUITHREADINFO { Size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return threadId != 0 && GetGUIThreadInfo(threadId, ref info) ? info.Focus : IntPtr.Zero;
    }

    public static uint GetIdentity(IntPtr window, out uint processId)
    {
        if (window == IntPtr.Zero || !IsWindow(window)) { processId = 0; return 0; }
        return GetWindowThreadProcessId(window, out processId);
    }

    public static int GetRelationship(IntPtr topLevelWindow, IntPtr candidate)
    {
        if (candidate == IntPtr.Zero) return 0;
        if (candidate == topLevelWindow) return 1;
        return IsChild(topLevelWindow, candidate) ? 2 : 3;
    }
}
'@

$diagnosticSuffixes = @(
    'Generation', 'State', 'Flags', 'Frequency', 'LastEndedGeneration', 'LastRestoreStatus',
    'RestoreGeneration', 'RestoreHideReason',
    'RestoreTargetWindow', 'RestoreTargetProcessId', 'RestoreTargetThreadId',
    'RestoreTargetFocusWindow', 'RestoreTargetFocusProcessId', 'RestoreTargetFocusThreadId',
    'RestoreTargetFocusRelationship', 'RestoreHideRequestedTimestamp', 'RestoreWindowHiddenTimestamp',
    'RestoreTopmostExitAttemptedTimestamp', 'RestoreTopmostExitCompletedTimestamp',
    'RestoreTopmostExitSucceeded', 'RestoreCalledTimestamp', 'RestoreReturnedTimestamp',
    'RestoreFaulted',
    'RestoreObservedForeground', 'RestoreObservedFocus', 'RestoreSetForegroundAttempted',
    'RestoreSetForegroundSucceeded', 'RestoreTargetWindowValid', 'RestoreTargetIdentityMatches',
    'RestoreCurrentTargetProcessId', 'RestoreCurrentTargetThreadId',
    'RestoreCapturedFocusWindowValid', 'RestoreCapturedFocusIdentityMatches',
    'RestoreCurrentCapturedFocusProcessId', 'RestoreCurrentCapturedFocusThreadId',
    'RestoreCurrentCapturedFocusRelationship', 'RestoreObservedFocusProcessId',
    'RestoreObservedFocusThreadId', 'RestoreObservedFocusRelationship'
)

$hideReasonNames = @('Hotkey', 'Escape', 'UserAction', 'ApplicationExit', 'ShowFailure')
$restoreStatusNames = @('Confirmed', 'AlreadyForeground', 'ForegroundConfirmedFocusChanged', 'InvalidTarget', 'RequestDenied', 'DifferentForeground')
$relationshipNames = @('None', 'SameWindow', 'Descendant', 'Unrelated')

function Get-EncodedDiagnosticValue {
    param([IntPtr] $Window, [string] $Suffix)
    $raw = [VokFocusRestoreRecorderNative]::GetRawDiagnostic($Window, $Suffix)
    if ($raw -eq 0) { return -1L }
    $raw - 1L
}

function Read-CoherentDiagnostic {
    param([IntPtr] $Window)
    for ($attempt = 0; $attempt -lt 64; $attempt++) {
        $before = [VokFocusRestoreRecorderNative]::GetRawDiagnostic($Window, 'Sequence')
        if ($before -eq 0 -or ($before -band 1) -ne 0) { continue }
        $values = [ordered]@{ Sequence = $before }
        foreach ($suffix in $diagnosticSuffixes) {
            $values[$suffix] = Get-EncodedDiagnosticValue $Window $suffix
        }
        $after = [VokFocusRestoreRecorderNative]::GetRawDiagnostic($Window, 'Sequence')
        if ($before -eq $after -and ($after -band 1) -eq 0) { return [pscustomobject]$values }
    }
    $null
}

function Convert-EnumValue {
    param([long] $Value, [string[]] $Names)
    if ($Value -ge 0 -and $Value -lt $Names.Count) { return $Names[$Value] }
    "Unknown($Value)"
}

function Convert-ToMilliseconds {
    param([long] $Timestamp, [long] $Origin, [long] $Frequency)
    if ($Timestamp -le 0 -or $Origin -le 0 -or $Frequency -le 0) { return $null }
    [Math]::Round((($Timestamp - $Origin) * 1000.0) / [double]$Frequency, 3)
}

function Get-ExternalObservation {
    param([pscustomobject] $Diagnostic)
    $foreground = [VokFocusRestoreRecorderNative]::GetForegroundWindow()
    $focus = [VokFocusRestoreRecorderNative]::GetFocus([uint32]$Diagnostic.RestoreTargetThreadId)
    [uint32] $foregroundProcessId = 0
    [uint32] $focusProcessId = 0
    $foregroundThreadId = [VokFocusRestoreRecorderNative]::GetIdentity($foreground, [ref]$foregroundProcessId)
    $focusThreadId = [VokFocusRestoreRecorderNative]::GetIdentity($focus, [ref]$focusProcessId)
    [pscustomobject]@{
        ProbeTimestamp = [Diagnostics.Stopwatch]::GetTimestamp()
        ForegroundWindow = $foreground.ToInt64()
        ForegroundProcessId = $foregroundProcessId
        ForegroundThreadId = $foregroundThreadId
        TargetThreadFocusWindow = $focus.ToInt64()
        TargetThreadFocusProcessId = $focusProcessId
        TargetThreadFocusThreadId = $focusThreadId
        TargetThreadFocusRelationship = Convert-EnumValue ([VokFocusRestoreRecorderNative]::GetRelationship(
            [IntPtr]$Diagnostic.RestoreTargetWindow,
            $focus)) $relationshipNames
    }
}

function Get-ProvisionalClassification {
    param([pscustomobject] $Diagnostic, [object[]] $Observations)
    if ($Diagnostic.RestoreFaulted -eq 1) { return 'A' }

    $validCaptureRelationships = @(1L, 2L)
    $captureInvalid =
        $Diagnostic.RestoreTargetWindow -eq 0 -or
        $Diagnostic.RestoreTargetProcessId -eq 0 -or
        $Diagnostic.RestoreTargetThreadId -eq 0 -or
        $Diagnostic.RestoreTargetFocusWindow -eq 0 -or
        $Diagnostic.RestoreTargetFocusProcessId -eq 0 -or
        $Diagnostic.RestoreTargetFocusThreadId -eq 0 -or
        $validCaptureRelationships -notcontains $Diagnostic.RestoreTargetFocusRelationship -or
        $Diagnostic.RestoreTargetWindowValid -ne 1 -or
        $Diagnostic.RestoreTargetIdentityMatches -ne 1 -or
        $Diagnostic.RestoreCapturedFocusWindowValid -ne 1 -or
        $Diagnostic.RestoreCapturedFocusIdentityMatches -ne 1 -or
        $validCaptureRelationships -notcontains $Diagnostic.RestoreCurrentCapturedFocusRelationship
    if ($captureInvalid) { return 'C' }

    if ($Diagnostic.RestoreObservedForeground -ne $Diagnostic.RestoreTargetWindow -or
        $Diagnostic.LastRestoreStatus -in @(4L, 5L)) { return 'A' }

    if ($Diagnostic.RestoreObservedFocus -ne $Diagnostic.RestoreTargetFocusWindow) { return 'B' }

    $laterLoss = @($Observations | Where-Object {
        $_.ForegroundWindow -ne $Diagnostic.RestoreTargetWindow -or
        $_.TargetThreadFocusWindow -ne $Diagnostic.RestoreTargetFocusWindow
    }).Count -gt 0
    if ($laterLoss) { return 'D' }
    'None'
}

function New-CausalRecord {
    param([pscustomobject] $Diagnostic, [object[]] $Observations)
    $origin = $Diagnostic.RestoreHideRequestedTimestamp
    $frequency = $Diagnostic.Frequency
    [pscustomobject]@{
        Generation = $Diagnostic.RestoreGeneration
        HideReason = Convert-EnumValue $Diagnostic.RestoreHideReason $hideReasonNames
        ProvisionalClassification = Get-ProvisionalClassification $Diagnostic $Observations
        CapturedTarget = [pscustomobject]@{
            TopLevelWindow = $Diagnostic.RestoreTargetWindow
            ProcessId = $Diagnostic.RestoreTargetProcessId
            ThreadId = $Diagnostic.RestoreTargetThreadId
            FocusedWindow = $Diagnostic.RestoreTargetFocusWindow
            FocusedProcessId = $Diagnostic.RestoreTargetFocusProcessId
            FocusedThreadId = $Diagnostic.RestoreTargetFocusThreadId
            FocusedRelationship = Convert-EnumValue $Diagnostic.RestoreTargetFocusRelationship $relationshipNames
        }
        Timeline = [pscustomobject]@{
            HideRequested = [pscustomobject]@{ Timestamp = $Diagnostic.RestoreHideRequestedTimestamp; SinceHideMs = 0.0 }
            WindowHidden = [pscustomobject]@{ Timestamp = $Diagnostic.RestoreWindowHiddenTimestamp; SinceHideMs = Convert-ToMilliseconds $Diagnostic.RestoreWindowHiddenTimestamp $origin $frequency }
            TopmostExitAttempted = [pscustomobject]@{ Timestamp = $Diagnostic.RestoreTopmostExitAttemptedTimestamp; SinceHideMs = Convert-ToMilliseconds $Diagnostic.RestoreTopmostExitAttemptedTimestamp $origin $frequency }
            TopmostExitCompleted = [pscustomobject]@{ Timestamp = $Diagnostic.RestoreTopmostExitCompletedTimestamp; SinceHideMs = Convert-ToMilliseconds $Diagnostic.RestoreTopmostExitCompletedTimestamp $origin $frequency; Succeeded = $Diagnostic.RestoreTopmostExitSucceeded -eq 1 }
            RestoreCalled = [pscustomobject]@{ Timestamp = $Diagnostic.RestoreCalledTimestamp; SinceHideMs = Convert-ToMilliseconds $Diagnostic.RestoreCalledTimestamp $origin $frequency }
            RestoreReturned = [pscustomobject]@{ Timestamp = $Diagnostic.RestoreReturnedTimestamp; SinceHideMs = Convert-ToMilliseconds $Diagnostic.RestoreReturnedTimestamp $origin $frequency }
        }
        RestoreValidation = [pscustomobject]@{
            TargetWindowValid = $Diagnostic.RestoreTargetWindowValid -eq 1
            TargetIdentityMatches = $Diagnostic.RestoreTargetIdentityMatches -eq 1
            CurrentTargetProcessId = $Diagnostic.RestoreCurrentTargetProcessId
            CurrentTargetThreadId = $Diagnostic.RestoreCurrentTargetThreadId
            CapturedFocusWindowValid = $Diagnostic.RestoreCapturedFocusWindowValid -eq 1
            CapturedFocusIdentityMatches = $Diagnostic.RestoreCapturedFocusIdentityMatches -eq 1
            CurrentCapturedFocusProcessId = $Diagnostic.RestoreCurrentCapturedFocusProcessId
            CurrentCapturedFocusThreadId = $Diagnostic.RestoreCurrentCapturedFocusThreadId
            CurrentCapturedFocusRelationship = Convert-EnumValue $Diagnostic.RestoreCurrentCapturedFocusRelationship $relationshipNames
        }
        SetForegroundWindow = [pscustomobject]@{
            Attempted = $Diagnostic.RestoreSetForegroundAttempted -eq 1
            BooleanResult = $Diagnostic.RestoreSetForegroundSucceeded -eq 1
            RestoreInvocationFaulted = $Diagnostic.RestoreFaulted -eq 1
        }
        ImmediateResult = [pscustomobject]@{
            ObservedForeground = $Diagnostic.RestoreObservedForeground
            ObservedFocus = $Diagnostic.RestoreObservedFocus
            ObservedFocusProcessId = $Diagnostic.RestoreObservedFocusProcessId
            ObservedFocusThreadId = $Diagnostic.RestoreObservedFocusThreadId
            ObservedFocusRelationship = Convert-EnumValue $Diagnostic.RestoreObservedFocusRelationship $relationshipNames
            Status = Convert-EnumValue $Diagnostic.LastRestoreStatus $restoreStatusNames
        }
        FinalHost = [pscustomobject]@{
            State = $Diagnostic.State
            CurrentGeneration = $Diagnostic.Generation
            LastEndedGeneration = $Diagnostic.LastEndedGeneration
            Flags = $Diagnostic.Flags
            Visible = ($Diagnostic.Flags -band 1) -ne 0
            Topmost = ($Diagnostic.Flags -band 2) -ne 0
            Foreground = ($Diagnostic.Flags -band 4) -ne 0
            Win32Focus = ($Diagnostic.Flags -band 8) -ne 0
            WpfAnchorFocus = ($Diagnostic.Flags -band 16) -ne 0
        }
        PostRestoreObservations = $Observations
    }
}

function Wait-ForRestoreRecord {
    param([IntPtr] $Window, [long] $AfterGeneration)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        $diagnostic = Read-CoherentDiagnostic $Window
        if ($null -ne $diagnostic -and
            $diagnostic.RestoreGeneration -gt $AfterGeneration -and
            $diagnostic.State -eq 0 -and
            $diagnostic.Generation -eq 0 -and
            $diagnostic.LastEndedGeneration -eq $diagnostic.RestoreGeneration -and
            $diagnostic.RestoreReturnedTimestamp -gt 0) {
            return $diagnostic
        }
        Start-Sleep -Milliseconds 5
    }
    throw "Timed out waiting for a completed physical hide after generation $AfterGeneration."
}

function Observe-PostRestore {
    param([pscustomobject] $Diagnostic)
    $samples = @()
    $lastKey = $null
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalMilliseconds -lt $PostRestoreObservationMilliseconds) {
        $sample = Get-ExternalObservation $Diagnostic
        $key = "$($sample.ForegroundWindow):$($sample.TargetThreadFocusWindow)"
        if ($key -ne $lastKey) { $samples += $sample; $lastKey = $key }
        Start-Sleep -Milliseconds 5
    }
    $samples
}

$running = @()
$candidateProcesses = @()
$pathInspectionDenied = $false
$window = [IntPtr]::Zero
$attachTimer = [Diagnostics.Stopwatch]::StartNew()
while ($attachTimer.Elapsed.TotalSeconds -lt $AttachTimeoutSeconds) {
    $candidateProcesses = @(Get-Process -Name Vok -ErrorAction SilentlyContinue)
    $pathInspectionDenied = $false
    $matchingProcesses = @()
    foreach ($candidateProcess in $candidateProcesses) {
        try {
            if ([StringComparer]::OrdinalIgnoreCase.Equals($candidateProcess.Path, $appPath)) {
                $matchingProcesses += $candidateProcess
            }
        }
        catch {
            $pathInspectionDenied = $true
        }
    }
    $running = @($matchingProcesses)
    if ($running.Count -eq 1) {
        $window = [VokFocusRestoreRecorderNative]::FindLargestWindow([uint32]$running[0].Id)
        if ($window -ne [IntPtr]::Zero) { break }
    }
    Start-Sleep -Milliseconds 50
}
if ($running.Count -ne 1) {
    if ($pathInspectionDenied -and $candidateProcesses.Count -gt 0) {
        throw "Vok is running, but the recorder cannot inspect its executable path. Start Vok and the recorder at the same integrity level."
    }
    throw "Expected exactly one Vok process from '$appPath'. Restart Vok with tools\Restart-Vok.cmd first."
}
if ($window -eq [IntPtr]::Zero) {
    throw 'Vok is running, but the recorder could not enumerate its top-level HWND. Start Vok and the recorder at the same integrity level.'
}
$initial = $null
$contractTimer = [Diagnostics.Stopwatch]::StartNew()
while ($contractTimer.Elapsed.TotalSeconds -lt $AttachTimeoutSeconds -and $null -eq $initial) {
    $initial = Read-CoherentDiagnostic $window
    if ($null -eq $initial) { Start-Sleep -Milliseconds 50 }
}
if ($null -eq $initial) {
    throw 'The running Vok build does not expose a readable focus-restore diagnostic contract.'
}
if ($initial.RestoreGeneration -lt 0 -or $initial.RestoreTargetFocusRelationship -lt 0 -or
    $initial.RestoreSetForegroundAttempted -lt 0) {
    throw 'The running Vok build exposes only the older host contract. Restart the freshly built Release version.'
}

$recorderState.ProcessId = $running[0].Id
$recorderState.DiagnosticWindow = $window.ToInt64()
$recorderState.Status = 'Armed'
Save-RecorderState
Write-Host 'Recorder armed. Keep this console in the background.'
Write-Host '1) Focus the same external text box; show Vok; hide with Ctrl+Alt+Space.'
Write-Host '2) Wait two seconds. Focus that text box again; show Vok; hide with Esc.'
Write-Host 'The recorder does not activate windows or call any restore API.'

$records = @()
$lastGeneration = [long]$initial.LastEndedGeneration
foreach ($expectedReason in @('Hotkey', 'Escape')) {
    $diagnostic = Wait-ForRestoreRecord $window $lastGeneration
    $observations = @(Observe-PostRestore $diagnostic)
    $record = New-CausalRecord $diagnostic $observations
    if ($record.HideReason -ne $expectedReason) {
        $recorderState.Records = @($records) + @($record)
        $recorderState.Status = "Unexpected$($record.HideReason)"
        Save-RecorderState
        throw "Expected $expectedReason hide but observed $($record.HideReason) for generation $($record.Generation)."
    }
    $records += $record
    $recorderState.Records = $records
    $recorderState.Status = "Captured$expectedReason"
    Save-RecorderState
    $lastGeneration = $record.Generation
    Write-Host "Captured generation $($record.Generation): $($record.HideReason), provisional class $($record.ProvisionalClassification)."
}

$recorderState.Status = 'Completed'
$recorderState.Records = $records
Save-RecorderState
Write-Host "Saved: $OutputPath" -ForegroundColor Green
Write-Host 'Send this JSON file back for A/B/C/D review. M0 remains not passed.'
