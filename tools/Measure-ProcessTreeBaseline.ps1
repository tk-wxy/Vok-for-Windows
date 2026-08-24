[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Label,
    [Parameter(Mandatory)] [string] $AppPath,
    [ValidateRange(1, 600)] [int] $WarmupSeconds = 30,
    [ValidateRange(2, 60)] [int] $SampleCount = 6,
    [ValidateRange(1, 30)] [int] $IntervalSeconds = 2,
    [ValidateSet('default', 'enabled', 'disabled')] [string] $PrecreateHwnd = 'default'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class VokProcessResourceProbe
{
    [DllImport("user32.dll")]
    public static extern uint GetGuiResources(IntPtr process, uint flags);
}
'@

function Get-ProcessTreeIds {
    param([Parameter(Mandatory)] [int] $RootId)

    $processes = @(Get-CimInstance Win32_Process | Select-Object ProcessId, ParentProcessId)
    $ids = [Collections.Generic.HashSet[int]]::new()
    [void] $ids.Add($RootId)
    do {
        $added = $false
        foreach ($process in $processes) {
            if ($ids.Contains([int] $process.ParentProcessId) -and $ids.Add([int] $process.ProcessId)) {
                $added = $true
            }
        }
    } while ($added)

    return @($ids)
}

function Get-TreeSnapshot {
    param([Parameter(Mandatory)] [int] $RootId)

    $ids = @(Get-ProcessTreeIds -RootId $RootId)
    $members = @(foreach ($id in $ids) {
        Get-Process -Id $id -ErrorAction SilentlyContinue
    })
    if (-not $members) {
        throw "Process tree rooted at PID $RootId no longer exists."
    }

    $workingSet = 0L
    $privateBytes = 0L
    $handles = 0
    $threads = 0
    $userObjects = 0L
    $gdiObjects = 0L
    foreach ($process in $members) {
        $process.Refresh()
        $workingSet += $process.WorkingSet64
        $privateBytes += $process.PrivateMemorySize64
        $handles += $process.HandleCount
        $threads += $process.Threads.Count
        $userObjects += [VokProcessResourceProbe]::GetGuiResources($process.Handle, 1)
        $gdiObjects += [VokProcessResourceProbe]::GetGuiResources($process.Handle, 0)
    }

    [pscustomobject]@{
        Timestamp = [DateTimeOffset]::Now
        ProcessCount = $members.Count
        WorkingSetMiB = [Math]::Round($workingSet / 1MB, 3)
        PrivateMiB = [Math]::Round($privateBytes / 1MB, 3)
        HandleCount = $handles
        ThreadCount = $threads
        UserObjectCount = $userObjects
        GdiObjectCount = $gdiObjects
    }
}

function Get-Average {
    param([object[]] $Samples, [string] $Property)
    [Math]::Round(($Samples | Measure-Object -Property $Property -Average).Average, 3)
}

$resolvedAppPath = (Resolve-Path -LiteralPath $AppPath).Path
$started = $null
try {
    $environment = @{}
    if ($PrecreateHwnd -eq 'enabled') {
        $environment['VOK_M0_PRECREATE_HWND'] = '1'
    }
    elseif ($PrecreateHwnd -eq 'disabled') {
        $environment['VOK_M0_PRECREATE_HWND'] = '0'
    }

    $startArguments = @{
        FilePath = $resolvedAppPath
        WorkingDirectory = (Split-Path -Parent $resolvedAppPath)
        PassThru = $true
    }
    if ($environment.Count -gt 0) {
        $startArguments.Environment = $environment
    }

    $started = Start-Process @startArguments
    $started.Refresh()
    if ($started.HasExited) {
        throw "$Label exited immediately with code $($started.ExitCode)."
    }

    $warmupDeadline = [DateTimeOffset]::Now.AddSeconds($WarmupSeconds)
    while ([DateTimeOffset]::Now -lt $warmupDeadline) {
        Start-Sleep -Milliseconds 500
        $started.Refresh()
        if ($started.HasExited) {
            throw "$Label exited during warmup with code $($started.ExitCode)."
        }
    }

    $samples = @()
    for ($index = 0; $index -lt $SampleCount; $index++) {
        $samples += Get-TreeSnapshot -RootId $started.Id
        if ($index -lt $SampleCount - 1) {
            Start-Sleep -Seconds $IntervalSeconds
        }
    }

    [pscustomobject]@{
        Schema = 1
        Label = $Label
        AppPath = $resolvedAppPath
        ArtifactLastWriteTime = (Get-Item -LiteralPath $resolvedAppPath).LastWriteTime
        WarmupSeconds = $WarmupSeconds
        SampleCount = $SampleCount
        IntervalSeconds = $IntervalSeconds
        PrecreateHwnd = $PrecreateHwnd
        Average = [pscustomobject]@{
            ProcessCount = Get-Average $samples 'ProcessCount'
            WorkingSetMiB = Get-Average $samples 'WorkingSetMiB'
            PrivateMiB = Get-Average $samples 'PrivateMiB'
            HandleCount = Get-Average $samples 'HandleCount'
            ThreadCount = Get-Average $samples 'ThreadCount'
            UserObjectCount = Get-Average $samples 'UserObjectCount'
            GdiObjectCount = Get-Average $samples 'GdiObjectCount'
        }
        Samples = $samples
    } | ConvertTo-Json -Depth 5
}
finally {
    if ($null -ne $started) {
        $treeIds = @(Get-ProcessTreeIds -RootId $started.Id | Sort-Object -Descending)
        foreach ($id in $treeIds) {
            Stop-Process -Id $id -Force -ErrorAction SilentlyContinue
        }
    }
}
