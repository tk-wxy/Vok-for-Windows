[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Label,
    [Parameter(Mandatory)] [string] $AppPath,
    [ValidateSet('control-space', 'control-alt-space')] [string] $Gesture,
    [ValidateRange(1, 1000)] [int] $Cycles = 60,
    [ValidateRange(30, 200)] [int] $HoldMilliseconds = 80,
    [ValidateRange(0, 30000)] [int] $WarmupMilliseconds = 2000,
    [ValidateRange(1, 100)] [int] $ResourceSampleEvery = 25,
    [ValidateSet('required', 'none')] [string] $HostDiagnostics = 'required',
    [ValidateSet('default', 'enabled', 'disabled')] [string] $PrecreateHwnd = 'default',
    [switch] $Measure30SecondRecovery,
    [switch] $MeasureFiveMinuteRecovery,
    [switch] $SampleSparsePixels,
    [string] $OutputPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class VokHostInteractionProbe
{
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModNoRepeat = 0x4000;
    public const byte VkControl = 0x11;
    public const byte VkMenu = 0x12;
    public const byte VkSpace = 0x20;
    public const uint KeyUp = 0x0002;
    public const uint MonitorDefaultToNearest = 0x00000002;
    public const int GwlExStyle = -20;
    public const long WsExTopmost = 0x00000008L;
    public const long AllReadyFlags = 0x1f;
    private const string Prefix = "Vok.M0.Diagnostics.";

    public sealed class WindowSnapshot
    {
        public IntPtr Handle { get; set; }
        public bool Visible { get; set; }
        public RECT Rect { get; set; }
    }

    public sealed class DiagnosticSnapshot
    {
        public long Sequence { get; set; }
        public long Generation { get; set; }
        public long State { get; set; }
        public long Flags { get; set; }
        public long Frequency { get; set; }
        public long HotkeyReceivedTimestamp { get; set; }
        public long FirstFrameTimestamp { get; set; }
        public long InputReadyTimestamp { get; set; }
        public long LastEndedGeneration { get; set; }
        public long LastHideRequestedTimestamp { get; set; }
        public long LastRestoreCompletedTimestamp { get; set; }
        public long LastRestoreStatus { get; set; }
        public bool IsFiveGateVisible { get { return State == 2 && Flags == AllReadyFlags && InputReadyTimestamp > 0; } }
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public uint Size;
        public RECT Monitor;
        public RECT Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct GUITHREADINFO
    {
        public uint Size;
        public uint Flags;
        public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret;
        public RECT CaretRect;
    }

    private delegate bool EnumWindowsCallback(IntPtr window, IntPtr parameter);

    [DllImport("user32.dll", SetLastError = true)] private static extern bool EnumWindows(EnumWindowsCallback callback, IntPtr parameter);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetWindowRect(IntPtr window, out RECT rect);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll", SetLastError = true)] public static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] public static extern void keybd_event(byte virtualKey, byte scanCode, uint flags, UIntPtr extraInfo);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool GetGUIThreadInfo(uint threadId, ref GUITHREADINFO info);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetProp(IntPtr window, string name);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);
    [DllImport("user32.dll")] public static extern uint GetGuiResources(IntPtr process, uint flags);

    public static WindowSnapshot FindLargestWindow(uint[] processIds)
    {
        WindowSnapshot largest = null;
        long largestArea = -1;
        EnumWindows((window, _) =>
        {
            uint owner;
            RECT rect;
            GetWindowThreadProcessId(window, out owner);
            if (Array.IndexOf(processIds, owner) < 0 || !GetWindowRect(window, out rect)) return true;
            long area = Math.Max(0, rect.Right - rect.Left) * (long)Math.Max(0, rect.Bottom - rect.Top);
            if (area > largestArea)
            {
                largestArea = area;
                largest = new WindowSnapshot { Handle = window, Visible = IsWindowVisible(window), Rect = rect };
            }
            return true;
        }, IntPtr.Zero);
        return largest;
    }

    public static WindowSnapshot InspectWindow(IntPtr window)
    {
        RECT rect;
        if (!GetWindowRect(window, out rect)) return null;
        return new WindowSnapshot { Handle = window, Visible = IsWindowVisible(window), Rect = rect };
    }

    public static RECT GetMonitorRect(IntPtr window)
    {
        IntPtr monitor = MonitorFromWindow(window, MonitorDefaultToNearest);
        var info = new MONITORINFO { Size = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Monitor lookup failed.");
        return info.Monitor;
    }

    public static IntPtr GetKeyboardFocusWindow()
    {
        IntPtr foreground = GetForegroundWindow();
        uint ignoredProcessId;
        uint thread = GetWindowThreadProcessId(foreground, out ignoredProcessId);
        var info = new GUITHREADINFO { Size = (uint)Marshal.SizeOf<GUITHREADINFO>() };
        return thread != 0 && GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }

    public static bool IsTopmost(IntPtr window)
    {
        return (GetWindowLongPtr(window, GwlExStyle).ToInt64() & WsExTopmost) != 0;
    }

    public static DiagnosticSnapshot ReadDiagnostic(IntPtr window)
    {
        for (int attempt = 0; attempt < 64; attempt++)
        {
            long sequenceBefore = GetRaw(window, "Sequence");
            if (sequenceBefore == 0 || (sequenceBefore & 1) != 0) continue;
            var snapshot = new DiagnosticSnapshot
            {
                Sequence = sequenceBefore,
                Generation = GetEncoded(window, "Generation"),
                State = GetEncoded(window, "State"),
                Flags = GetEncoded(window, "Flags"),
                Frequency = GetEncoded(window, "Frequency"),
                HotkeyReceivedTimestamp = GetEncoded(window, "HotkeyReceivedTimestamp"),
                FirstFrameTimestamp = GetEncoded(window, "FirstFrameTimestamp"),
                InputReadyTimestamp = GetEncoded(window, "InputReadyTimestamp"),
                LastEndedGeneration = GetEncoded(window, "LastEndedGeneration"),
                LastHideRequestedTimestamp = GetEncoded(window, "LastHideRequestedTimestamp"),
                LastRestoreCompletedTimestamp = GetEncoded(window, "LastRestoreCompletedTimestamp"),
                LastRestoreStatus = GetEncoded(window, "LastRestoreStatus"),
            };
            long sequenceAfter = GetRaw(window, "Sequence");
            if (sequenceBefore == sequenceAfter && (sequenceAfter & 1) == 0) return snapshot;
        }
        return null;
    }

    public static string SamplePixels(RECT rect)
    {
        int width = Math.Max(1, rect.Right - rect.Left);
        int height = Math.Max(1, rect.Bottom - rect.Top);
        int[,] points = { { rect.Left + 20, rect.Top + 20 }, { rect.Left + width / 2, rect.Top + height / 2 }, { rect.Right - 20, rect.Bottom - 20 } };
        IntPtr dc = GetDC(IntPtr.Zero);
        try
        {
            string result = "";
            for (int i = 0; i < points.GetLength(0); i++) result += GetPixel(dc, points[i, 0], points[i, 1]).ToString("X8") + ":";
            return result;
        }
        finally { if (dc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, dc); }
    }

    private static long GetRaw(IntPtr window, string suffix)
    {
        return GetProp(window, Prefix + suffix).ToInt64();
    }
    private static long GetEncoded(IntPtr window, string suffix)
    {
        long raw = GetRaw(window, suffix);
        return raw == 0 ? -1 : raw - 1;
    }
}
'@

$probeHotkeyId = 0x5650
$timeout = [TimeSpan]::FromSeconds(5)
$pollMilliseconds = 5

function Wait-Until {
    param([scriptblock] $Condition, [string] $FailureMessage)
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed -lt $timeout) {
        $value = & $Condition
        if ($value) { return $value }
        Start-Sleep -Milliseconds $pollMilliseconds
    }
    throw $FailureMessage
}

function Get-ProcessTreeIds {
    param([int] $RootId)
    $rows = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
    $ids = [Collections.Generic.HashSet[int]]::new()
    [void] $ids.Add($RootId)
    do {
        $added = $false
        foreach ($row in $rows) {
            if ($ids.Contains([int]$row.ParentProcessId) -and $ids.Add([int]$row.ProcessId)) { $added = $true }
        }
    } while ($added)
    @($ids)
}

function Send-Gesture {
    if ($Gesture -eq 'control-alt-space') { [VokHostInteractionProbe]::keybd_event([VokHostInteractionProbe]::VkMenu, 0, 0, [UIntPtr]::Zero) }
    [VokHostInteractionProbe]::keybd_event([VokHostInteractionProbe]::VkControl, 0, 0, [UIntPtr]::Zero)
    [VokHostInteractionProbe]::keybd_event([VokHostInteractionProbe]::VkSpace, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds $HoldMilliseconds
    [VokHostInteractionProbe]::keybd_event([VokHostInteractionProbe]::VkSpace, 0, [VokHostInteractionProbe]::KeyUp, [UIntPtr]::Zero)
    [VokHostInteractionProbe]::keybd_event([VokHostInteractionProbe]::VkControl, 0, [VokHostInteractionProbe]::KeyUp, [UIntPtr]::Zero)
    if ($Gesture -eq 'control-alt-space') { [VokHostInteractionProbe]::keybd_event([VokHostInteractionProbe]::VkMenu, 0, [VokHostInteractionProbe]::KeyUp, [UIntPtr]::Zero) }
}

function Test-OwnerInTree {
    param([IntPtr] $Window, [int[]] $TreeIds)
    if ($Window -eq [IntPtr]::Zero) { return $false }
    [uint32] $owner = 0
    [void][VokHostInteractionProbe]::GetWindowThreadProcessId($Window, [ref]$owner)
    $TreeIds -contains $owner
}

function Get-TreeResourceSnapshot {
    param([int[]] $TreeIds, [string] $Phase, [int] $Cycle)
    [long] $workingSet = 0; [long] $privateBytes = 0; [long] $handles = 0; [long] $threads = 0
    [long] $userObjects = 0; [long] $gdiObjects = 0; [int] $processCount = 0
    foreach ($processId in $TreeIds) {
        $process = Get-Process -Id $processId -ErrorAction SilentlyContinue
        if ($null -eq $process) { continue }
        try {
            $process.Refresh(); $processCount++
            $workingSet += $process.WorkingSet64; $privateBytes += $process.PrivateMemorySize64
            $handles += $process.HandleCount; $threads += $process.Threads.Count
            $userObjects += [VokHostInteractionProbe]::GetGuiResources($process.Handle, 1)
            $gdiObjects += [VokHostInteractionProbe]::GetGuiResources($process.Handle, 0)
        }
        finally { $process.Dispose() }
    }
    [pscustomobject]@{
        Timestamp = [DateTimeOffset]::Now; Phase = $Phase; Cycle = $Cycle; ProcessCount = $processCount
        WorkingSetMiB = [Math]::Round($workingSet / 1MB, 3); PrivateBytesMiB = [Math]::Round($privateBytes / 1MB, 3)
        Handles = $handles; Threads = $threads; UserObjects = $userObjects; GdiObjects = $gdiObjects
    }
}

function Get-PercentileSummary {
    param([object[]] $Rows, [string] $Property)
    $values = @($Rows | ForEach-Object { $_.$Property } | Where-Object { $null -ne $_ } | Sort-Object)
    if ($values.Count -eq 0) { return [pscustomobject]@{ Samples = 0; P50 = $null; P95 = $null; Maximum = $null } }
    $p50 = $values[[Math]::Max(0, [Math]::Ceiling($values.Count * 0.50) - 1)]
    $p95 = $values[[Math]::Max(0, [Math]::Ceiling($values.Count * 0.95) - 1)]
    [pscustomobject]@{ Samples = $values.Count; P50 = [Math]::Round($p50, 1); P95 = [Math]::Round($p95, 1); Maximum = [Math]::Round($values[-1], 1) }
}

function Get-LatencySetSummary {
    param([object[]] $Rows)
    $properties = @('ExternalVisibleMs','ExternalForegroundMs','ExternalWin32FocusMs','ExternalFiveGateReadyMs','ExternalHiddenMs','ExternalRestoreMs','InternalInputReadyMs','InternalFirstFrameMs','InternalRestoreMs')
    $result = [ordered]@{}
    foreach ($property in $properties) { $result[$property] = Get-PercentileSummary $Rows $property }
    [pscustomobject]$result
}

function Get-LinearSlope {
    param([object[]] $Samples, [string] $Property)
    if ($Samples.Count -lt 2) { return $null }
    $meanX = ($Samples | Measure-Object Cycle -Average).Average
    $meanY = ($Samples | Measure-Object $Property -Average).Average
    $numerator = 0.0; $denominator = 0.0
    foreach ($sample in $Samples) { $dx = $sample.Cycle - $meanX; $numerator += $dx * ($sample.$Property - $meanY); $denominator += $dx * $dx }
    if ($denominator -eq 0) { return $null }
    [Math]::Round($numerator / $denominator, 6)
}

$resolved = (Resolve-Path -LiteralPath $AppPath).Path
$modifierFlags = [VokHostInteractionProbe]::ModControl -bor [VokHostInteractionProbe]::ModNoRepeat
if ($Gesture -eq 'control-alt-space') { $modifierFlags = $modifierFlags -bor [VokHostInteractionProbe]::ModAlt }
$started = $null
$treeIds = @()
try {
    if (-not [VokHostInteractionProbe]::RegisterHotKey([IntPtr]::Zero, $probeHotkeyId, $modifierFlags, [VokHostInteractionProbe]::VkSpace)) { throw "$Gesture is already occupied before $Label starts." }
    [void][VokHostInteractionProbe]::UnregisterHotKey([IntPtr]::Zero, $probeHotkeyId)

    $startArguments = @{ FilePath = $resolved; WorkingDirectory = (Split-Path -Parent $resolved); PassThru = $true }
    $environmentOverrides = @{}
    if ($PrecreateHwnd -eq 'enabled') { $environmentOverrides.VOK_M0_PRECREATE_HWND = '1' }
    elseif ($PrecreateHwnd -eq 'disabled') { $environmentOverrides.VOK_M0_PRECREATE_HWND = '0' }
    if ($environmentOverrides.Count -gt 0) { $startArguments.Environment = $environmentOverrides }
    $started = Start-Process @startArguments

    $null = Wait-Until {
        $registered = [VokHostInteractionProbe]::RegisterHotKey([IntPtr]::Zero, $probeHotkeyId, $modifierFlags, [VokHostInteractionProbe]::VkSpace)
        if ($registered) { [void][VokHostInteractionProbe]::UnregisterHotKey([IntPtr]::Zero, $probeHotkeyId); return $false }
        [Runtime.InteropServices.Marshal]::GetLastWin32Error() -eq 1409
    } "$Label did not register $Gesture."

    $target = [VokHostInteractionProbe]::GetForegroundWindow()
    if ($target -eq [IntPtr]::Zero) { throw 'No external foreground target is available.' }
    $targetFocus = [VokHostInteractionProbe]::GetKeyboardFocusWindow()
    $monitorRect = [VokHostInteractionProbe]::GetMonitorRect($target)
    if ($WarmupMilliseconds -gt 0) { Start-Sleep -Milliseconds $WarmupMilliseconds }

    # Freeze the process set once. The hot loop never performs CIM/process-tree discovery.
    $treeIds = @((Get-ProcessTreeIds -RootId $started.Id) | ForEach-Object { [int]$_ })
    $treeIdsU32 = @($treeIds | ForEach-Object { [uint32]$_ })
    $window = Wait-Until { [VokHostInteractionProbe]::FindLargestWindow($treeIdsU32) } "$Label did not create a top-level HWND."
    $windowHandle = $window.Handle
    if ($HostDiagnostics -eq 'required') {
        $null = Wait-Until {
            $diagnostic = [VokHostInteractionProbe]::ReadDiagnostic($window.Handle)
            if ($null -ne $diagnostic -and $diagnostic.State -eq 0 -and $diagnostic.Generation -eq 0) { $diagnostic }
        } "$Label did not expose the Vok M0 diagnostic contract."
    }

    $baselinePixels = if ($SampleSparsePixels) { [VokHostInteractionProbe]::SamplePixels([VokHostInteractionProbe]::GetMonitorRect($target)) } else { $null }
    $resourceSamples = @()
    $resourceSamples += Get-TreeResourceSnapshot $treeIds 'never-shown-hidden-baseline' 0
    $cyclesResult = @()
    $failureCounts = [ordered]@{ Show = 0; FiveGate = 0; Hide = 0; Restore = 0; RestoreDegraded = 0; FocusRestore = 0 }
    $lastEndedGeneration = 0L

    for ($cycle = 1; $cycle -le $Cycles; $cycle++) {
        $sampleResources = $cycle -le 10 -or $cycle -eq $Cycles -or ($cycle % $ResourceSampleEvery) -eq 0
        $row = [ordered]@{ Cycle = $cycle; Phase = if ($cycle -le 10) { 'warmup' } else { 'steady' }; Failure = $null }
        try {
            $showTimer = [Diagnostics.Stopwatch]::StartNew(); Send-Gesture
            $shown = $window; $diagnosticReady = $null; $visibleMs = $null; $foregroundMs = $null; $focusMs = $null; $fiveGateMs = $null
            $surfaceMs = $null; $lastPixels = ''; $stableCount = 0; $nextPixelSampleMs = 0.0
            while ($showTimer.Elapsed -lt $timeout -and ($null -eq $fiveGateMs -or ($HostDiagnostics -eq 'required' -and $null -eq $diagnosticReady))) {
                if ([VokHostInteractionProbe]::IsWindowVisible($windowHandle)) {
                    if ($null -eq $visibleMs) { $visibleMs = $showTimer.Elapsed.TotalMilliseconds }
                    $foregroundInTree = Test-OwnerInTree ([VokHostInteractionProbe]::GetForegroundWindow()) $treeIds
                    if ($null -eq $foregroundMs -and $foregroundInTree) { $foregroundMs = $showTimer.Elapsed.TotalMilliseconds }
                    if ($null -eq $focusMs -and $foregroundInTree -and (Test-OwnerInTree ([VokHostInteractionProbe]::GetKeyboardFocusWindow()) $treeIds)) { $focusMs = $showTimer.Elapsed.TotalMilliseconds }
                    if ($HostDiagnostics -eq 'required') {
                        $diagnostic = [VokHostInteractionProbe]::ReadDiagnostic($windowHandle)
                        if ($null -ne $diagnostic -and $diagnostic.Generation -gt $lastEndedGeneration -and $diagnostic.IsFiveGateVisible) {
                            $diagnosticReady = $diagnostic; $fiveGateMs = $showTimer.Elapsed.TotalMilliseconds
                        }
                    }
                    elseif ($null -ne $visibleMs -and $null -ne $foregroundMs -and $null -ne $focusMs) { $fiveGateMs = $focusMs }
                    if ($SampleSparsePixels -and $null -eq $surfaceMs -and $showTimer.Elapsed.TotalMilliseconds -ge $nextPixelSampleMs) {
                        $pixels = [VokHostInteractionProbe]::SamplePixels(([VokHostInteractionProbe]::InspectWindow($windowHandle)).Rect)
                        if ($pixels -ne $baselinePixels -and $pixels -eq $lastPixels) { $stableCount++ } else { $stableCount = 0 }
                        if ($stableCount -ge 2) { $surfaceMs = $showTimer.Elapsed.TotalMilliseconds }
                        $lastPixels = $pixels; $nextPixelSampleMs = $showTimer.Elapsed.TotalMilliseconds + 10
                    }
                }
                Start-Sleep -Milliseconds $pollMilliseconds
            }
            if ($null -eq $visibleMs) { $failureCounts.Show++; throw "Visible HWND timeout on cycle $cycle." }
            if ($null -eq $fiveGateMs) { $failureCounts.FiveGate++; throw "Five-gate input-ready timeout on cycle $cycle." }
            $shown = [VokHostInteractionProbe]::InspectWindow($windowHandle)
            $row.FullScreen = $shown.Rect.Left -eq $monitorRect.Left -and $shown.Rect.Top -eq $monitorRect.Top -and $shown.Rect.Right -eq $monitorRect.Right -and $shown.Rect.Bottom -eq $monitorRect.Bottom
            if (-not $row.FullScreen) { throw "Window did not match the target monitor rectangle on cycle $cycle." }
            if ($HostDiagnostics -eq 'required') {
                $generation = $diagnosticReady.Generation; $lastEndedGeneration = $generation
                $frameDeadline = [Diagnostics.Stopwatch]::StartNew()
                while ($diagnosticReady.FirstFrameTimestamp -le 0 -and $frameDeadline.Elapsed -lt $timeout) {
                    $diagnosticReady = [VokHostInteractionProbe]::ReadDiagnostic($windowHandle)
                    Start-Sleep -Milliseconds $pollMilliseconds
                }
                if ($diagnosticReady.FirstFrameTimestamp -le 0) { throw "Internal first-frame timeout on cycle $cycle." }
                $frequency = [double]$diagnosticReady.Frequency
                $row.Generation = $generation
                $row.InternalInputReadyMs = [Math]::Round((($diagnosticReady.InputReadyTimestamp - $diagnosticReady.HotkeyReceivedTimestamp) * 1000.0) / $frequency, 1)
                $row.InternalFirstFrameMs = [Math]::Round((($diagnosticReady.FirstFrameTimestamp - $diagnosticReady.HotkeyReceivedTimestamp) * 1000.0) / $frequency, 1)
            }
            else { $generation = 0L; $row.Generation = $null; $row.InternalInputReadyMs = $null; $row.InternalFirstFrameMs = $null }
            $row.ExternalVisibleMs = [Math]::Round($visibleMs, 1); $row.ExternalForegroundMs = if ($null -eq $foregroundMs) { $null } else { [Math]::Round($foregroundMs, 1) }
            $row.ExternalWin32FocusMs = if ($null -eq $focusMs) { $null } else { [Math]::Round($focusMs, 1) }; $row.ExternalFiveGateReadyMs = [Math]::Round($fiveGateMs, 1)
            $row.SparsePixelStableMs = if ($null -eq $surfaceMs) { $null } else { [Math]::Round($surfaceMs, 1) }
            $row.SessionTopmost = [VokHostInteractionProbe]::IsTopmost($shown.Handle)
            if ($sampleResources) { $resourceSamples += Get-TreeResourceSnapshot $treeIds 'shown' $cycle }

            $hideTimer = [Diagnostics.Stopwatch]::StartNew(); Send-Gesture
            $hiddenMs = $null; $restoreMs = $null; $endedDiagnostic = $null
            while ($hideTimer.Elapsed -lt $timeout -and ($null -eq $hiddenMs -or $null -eq $restoreMs -or ($HostDiagnostics -eq 'required' -and $null -eq $endedDiagnostic))) {
                if ($null -eq $hiddenMs -and -not [VokHostInteractionProbe]::IsWindowVisible($windowHandle)) { $hiddenMs = $hideTimer.Elapsed.TotalMilliseconds }
                if ($HostDiagnostics -eq 'required') {
                    $diagnostic = [VokHostInteractionProbe]::ReadDiagnostic($windowHandle)
                    if ($null -ne $diagnostic -and $diagnostic.State -eq 0 -and $diagnostic.Generation -eq 0 -and $diagnostic.LastEndedGeneration -eq $generation) { $endedDiagnostic = $diagnostic }
                }
                if ($null -eq $restoreMs -and [VokHostInteractionProbe]::GetForegroundWindow() -eq $target) { $restoreMs = $hideTimer.Elapsed.TotalMilliseconds }
                Start-Sleep -Milliseconds $pollMilliseconds
            }
            if ($null -eq $hiddenMs) { $failureCounts.Hide++; throw "Hidden HWND timeout on cycle $cycle." }
            if ($null -eq $restoreMs) { $failureCounts.Restore++; throw "Foreground restore timeout on cycle $cycle." }
            if ($HostDiagnostics -eq 'required' -and $null -eq $endedDiagnostic) { $failureCounts.Hide++; throw "Generation $generation did not end cleanly on cycle $cycle." }
            $restoredFocus = [VokHostInteractionProbe]::GetKeyboardFocusWindow()
            $row.ExternalHiddenMs = [Math]::Round($hiddenMs, 1); $row.ExternalRestoreMs = [Math]::Round($restoreMs, 1)
            $row.OriginalFocusRestored = $targetFocus -eq [IntPtr]::Zero -or $restoredFocus -eq $targetFocus
            if (-not $row.OriginalFocusRestored) { $failureCounts.FocusRestore++ }
            $row.TopmostAfterHide = [VokHostInteractionProbe]::IsTopmost($windowHandle)
            if ($HostDiagnostics -eq 'required') {
                if ($endedDiagnostic.Flags -ne 0) { throw "Ended generation $generation retained physical host flags: $($endedDiagnostic.Flags)." }
                $row.InternalRestoreMs = if ($endedDiagnostic.LastRestoreCompletedTimestamp -gt 0) { [Math]::Round((($endedDiagnostic.LastRestoreCompletedTimestamp - $endedDiagnostic.LastHideRequestedTimestamp) * 1000.0) / [double]$endedDiagnostic.Frequency, 1) } else { $null }
                $row.RestoreStatus = $endedDiagnostic.LastRestoreStatus
                if ($endedDiagnostic.LastRestoreCompletedTimestamp -le 0) { $failureCounts.RestoreDegraded++ }
            }
            else { $row.InternalRestoreMs = $null; $row.RestoreStatus = $null }
            if ($sampleResources) { $resourceSamples += Get-TreeResourceSnapshot $treeIds 'hidden-immediate' $cycle }
        }
        catch {
            $row.Failure = $_.Exception.Message
            if ([VokHostInteractionProbe]::IsWindowVisible($windowHandle)) {
                Send-Gesture
                try { $null = Wait-Until { if (-not [VokHostInteractionProbe]::IsWindowVisible($windowHandle)) { $true } } 'Recovery hide failed.' } catch { }
            }
        }
        $cyclesResult += [pscustomobject]$row
    }

    $resourceSamples += Get-TreeResourceSnapshot $treeIds 'after-final-cycle-hidden' $Cycles
    $elapsedRecovery = 0
    $recoveryCheckpoints = @()
    if ($Measure30SecondRecovery) { $recoveryCheckpoints += 30 }
    if ($MeasureFiveMinuteRecovery) { $recoveryCheckpoints += 300 }
    foreach ($checkpoint in @($recoveryCheckpoints | Sort-Object -Unique)) {
        if ($checkpoint -gt $elapsedRecovery) { Start-Sleep -Seconds ($checkpoint - $elapsedRecovery); $elapsedRecovery = $checkpoint }
        $resourceSamples += Get-TreeResourceSnapshot $treeIds "hidden-recovery-${checkpoint}s" $Cycles
    }

    $successful = @($cyclesResult | Where-Object { $null -eq $_.Failure })
    $steadyResources = @($resourceSamples | Where-Object { $_.Phase -eq 'hidden-immediate' -and $_.Cycle -gt 10 })
    $tailResources = @($steadyResources | Where-Object Cycle -gt ([Math]::Floor($Cycles / 2)))
    $resourceProperties = @('WorkingSetMiB','PrivateBytesMiB','Handles','Threads','UserObjects','GdiObjects')
    $slopes = [ordered]@{}
    $tailSlopes = [ordered]@{}
    $platform = [ordered]@{}
    $peaks = [ordered]@{}
    foreach ($property in $resourceProperties) {
        $slopes[$property] = Get-LinearSlope $steadyResources $property
        $tailSlopes[$property] = Get-LinearSlope $tailResources $property
        $platform[$property] = (Get-PercentileSummary $steadyResources $property).P50
        $peaks[$property] = ($resourceSamples | Measure-Object $property -Maximum).Maximum
    }
    $result = [pscustomobject]@{
        Schema = 4; Label = $Label; AppPath = $resolved; ArtifactLastWriteTime = (Get-Item -LiteralPath $resolved).LastWriteTime
        Gesture = $Gesture; HoldMilliseconds = $HoldMilliseconds; HostDiagnostics = $HostDiagnostics; CyclesRequested = $Cycles
        SuccessfulCycles = $successful.Count; FailedCycles = $Cycles - $successful.Count; FailureCounts = [pscustomobject]$failureCounts
        Latency = [pscustomobject]@{
            All = Get-LatencySetSummary $successful
            First10Warmup = Get-LatencySetSummary @($successful | Where-Object Cycle -le 10)
            SteadyAfter10 = Get-LatencySetSummary @($successful | Where-Object Cycle -gt 10)
        }
        Resources = [pscustomobject]@{
            NeverShownHiddenBaseline = $resourceSamples | Where-Object Phase -eq 'never-shown-hidden-baseline' | Select-Object -First 1
            FirstUseHidden = $resourceSamples | Where-Object { $_.Phase -eq 'hidden-immediate' -and $_.Cycle -eq 1 } | Select-Object -First 1
            AfterFinalCycleHidden = $resourceSamples | Where-Object Phase -eq 'after-final-cycle-hidden' | Select-Object -Last 1
            Recovery = @($resourceSamples | Where-Object Phase -like 'hidden-recovery-*')
            SteadyHiddenPlatformP50 = [pscustomobject]$platform; Peak = [pscustomobject]$peaks
            SteadyAfter10SlopePerCycle = [pscustomobject]$slopes; TailHalfSlopePerCycle = [pscustomobject]$tailSlopes
            Samples = $resourceSamples
        }
        SparsePixelNote = if ($SampleSparsePixels) { 'Diagnostic only; not a formal present measurement.' } else { 'Disabled; formal present remains unverified without ETW/PresentMon.' }
        Cycles = $cyclesResult
    }
    $json = $result | ConvertTo-Json -Depth 8
    if ($OutputPath) {
        $parent = Split-Path -Parent $OutputPath
        if ($parent) { [IO.Directory]::CreateDirectory((Join-Path (Get-Location) $parent)) | Out-Null }
        [IO.File]::WriteAllText((Join-Path (Get-Location) $OutputPath), $json, [Text.UTF8Encoding]::new($false))
        [pscustomobject]@{
            Schema = $result.Schema; Label = $result.Label; Gesture = $result.Gesture
            CyclesRequested = $result.CyclesRequested; SuccessfulCycles = $result.SuccessfulCycles
            FailedCycles = $result.FailedCycles; FailureCounts = $result.FailureCounts; Latency = $result.Latency
            Resources = $result.Resources | Select-Object NeverShownHiddenBaseline, FirstUseHidden, AfterFinalCycleHidden, Recovery, SteadyHiddenPlatformP50, Peak, SteadyAfter10SlopePerCycle, TailHalfSlopePerCycle
            SparsePixelNote = $result.SparsePixelNote; RawOutputPath = $OutputPath
        } | ConvertTo-Json -Depth 8
    }
    else { $json }
}
finally {
    if ($null -ne $started) {
        foreach ($id in @($treeIds | Sort-Object -Descending)) { Stop-Process -Id $id -Force -ErrorAction SilentlyContinue }
    }
}
