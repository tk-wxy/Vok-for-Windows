[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Path)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$snapshot = Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
if ($snapshot.Version -ne 1) { throw "Unsupported reveal trace version: $($snapshot.Version)" }
$frequency = [double]$snapshot.TimestampFrequency
if ($frequency -le 0) { throw 'Invalid timestamp frequency.' }
$entries = @($snapshot.Entries | Sort-Object Timestamp, Sequence)

function Get-ElapsedMs($entry, [long] $start) {
    if ($null -eq $entry -or $start -le 0) { return $null }
    [Math]::Round(($entry.Timestamp - $start) * 1000.0 / $frequency, 3)
}

$sessions = @(foreach ($group in @($entries | Where-Object Generation -gt 0 | Group-Object Generation)) {
    $events = @($group.Group)
    $request = $events | Where-Object Phase -eq 'ShowRequested' | Select-Object -First 1
    $uncloaked = $events | Where-Object Phase -eq 'Uncloaked' | Select-Object -First 1
    $checkpoint = $events | Where-Object { $_.Phase -eq 'StageCheckpoint' -and $_.Detail -eq 'BeforeUncloak' } | Select-Object -First 1
    $ready = $events | Where-Object { $null -ne $_.Stage -and $_.Stage.StaticContentReady } | Select-Object -First 1
    $readyAfterUncloak = if ($null -ne $uncloaked) { $events | Where-Object { $_.Timestamp -ge $uncloaked.Timestamp -and $null -ne $_.Stage -and $_.Stage.StaticContentReady } | Select-Object -First 1 } else { $null }
    $start = if ($null -ne $request) { [long]$request.Value1 } else { 0L }
    $showBegin = $events | Where-Object Phase -eq 'ShowBegin' | Select-Object -First 1
    $rendering = $events | Where-Object Phase -eq 'Rendering' | Select-Object -First 1
    $contentReady = $events | Where-Object Phase -eq 'ContentReady' | Select-Object -First 1
    $fenceRequest = $events | Where-Object Phase -eq 'FenceRequested' | Select-Object -First 1
    $fenceReturn = $events | Where-Object Phase -eq 'FenceReturned' | Select-Object -First 1
    $inputReady = $events | Where-Object Phase -eq 'InputReady' | Select-Object -First 1
    [pscustomobject]@{
        Generation = [long]$group.Name
        HasShowRequest = $null -ne $request
        HiddenSeconds = if ($null -ne $request -and $request.Value2 -gt 0) { [Math]::Round(($request.Value1 - $request.Value2) / $frequency, 3) } else { $null }
        UiShowBeginMs = Get-ElapsedMs $showBegin $start
        ContentReadyMs = Get-ElapsedMs $contentReady $start
        RenderingMs = Get-ElapsedMs $rendering $start
        FenceRequestMs = Get-ElapsedMs $fenceRequest $start
        FenceReturnMs = Get-ElapsedMs $fenceReturn $start
        UncloakedMs = Get-ElapsedMs $uncloaked $start
        InputReadyMs = Get-ElapsedMs $inputReady $start
        StaticReadyObservedMs = Get-ElapsedMs $ready $start
        StaticReadyAtUncloak = if ($null -ne $checkpoint) { $checkpoint.Stage.StaticContentReady } else { $null }
        UncloakStageState = if ($null -ne $checkpoint) { $checkpoint.Stage } else { $null }
        ObservedReadinessLagMs = if ($null -ne $readyAfterUncloak -and $null -ne $checkpoint -and -not $checkpoint.Stage.StaticContentReady) { Get-ElapsedMs $readyAfterUncloak $uncloaked.Timestamp } else { $null }
        RowPublications = @($events | Where-Object Phase -eq 'StageRowsPublished').Count
        SourceAttachments = @($events | Where-Object Phase -eq 'StageSourceAttached').Count
        AvailabilityRequests = @($events | Where-Object Phase -eq 'StageAvailabilityRequested').Count
        AvailabilityRequestsBeforeUncloak = if ($null -ne $uncloaked) { @($events | Where-Object { $_.Phase -eq 'StageAvailabilityRequested' -and $_.Timestamp -lt $uncloaked.Timestamp }).Count } else { $null }
        AvailabilityApplications = @($events | Where-Object Phase -eq 'StageAvailabilityApplied').Count
        Failure = @($events | Where-Object Phase -eq 'RevealFailureReason' | Select-Object -ExpandProperty Detail)
    }
})

$jobs = @(foreach ($group in @($entries | Where-Object { $_.Phase -like 'Shell*' } | Group-Object OperationId)) {
    $attempt = $group.Group | Where-Object Phase -eq 'ShellEnqueueAttempt' | Select-Object -First 1
    $began = $group.Group | Where-Object Phase -eq 'ShellStarted' | Select-Object -First 1
    $ended = $group.Group | Where-Object { $_.Phase -in 'ShellCompleted','ShellFailed','ShellSkipped','ShellRejected' } | Select-Object -Last 1
    $first = $group.Group | Select-Object -First 1
    [pscustomobject]@{
        OperationId = [long]$group.Name
        Kind = $first.Detail
        ItemId = $first.ItemId
        Revision = $first.Revision
        QueueWaitMs = if ($null -ne $attempt) { Get-ElapsedMs $began $attempt.Timestamp } else { $null }
        ExecutionMs = if ($null -ne $began) { Get-ElapsedMs $ended $began.Timestamp } else { $null }
        Terminal = if ($null -ne $ended) { $ended.Phase } else { 'NotObserved' }
    }
})

[pscustomobject]@{
    ProcessId = $snapshot.ProcessId
    OverwrittenEntries = $snapshot.OverwrittenEntries
    LastResumeTimestamp = $snapshot.LastResumeTimestamp
    LastUnlockTimestamp = $snapshot.LastUnlockTimestamp
    Evidence = 'Managed readiness observations; not a physical Present or pixel-completeness assertion. Missing milestones remain null. Compare timestamps, not cross-thread append order.'
    Sessions = $sessions
    RecoveryEvents = @($entries | Where-Object { $_.Phase -in 'PowerMode','SessionSwitch','RecoveryObservationUnavailable' })
    ResourceSamples = @($entries | Where-Object Phase -eq 'ResourceSample')
    ShellJobs = $jobs
} | ConvertTo-Json -Depth 12
