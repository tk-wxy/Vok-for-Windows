[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ProbePath,
    [Parameter(Mandatory)] [string] $AllowedOutputRoot,
    [Parameter(Mandatory)] [string] $OutputRoot,
    [Parameter(Mandatory)] [string] $SourceRevision,
    [ValidateRange(1, 100000)] [int[]] $Counts = @(50, 10000, 100000),
    [ValidateRange(1, 5000)] [int] $PressureEdgeSteps = 512,
    [ValidateRange(640, 3840)] [double] $WindowWidth = 1500,
    [ValidateRange(480, 2160)] [double] $WindowHeight = 900
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Resolve-FullPath {
    param([Parameter(Mandatory)] [string] $Path)

    [IO.Path]::GetFullPath($Path)
}

function Assert-StrictChildPath {
    param(
        [Parameter(Mandatory)] [string] $Candidate,
        [Parameter(Mandatory)] [string] $Root,
        [Parameter(Mandatory)] [string] $Description
    )

    $relative = [IO.Path]::GetRelativePath($Root, $Candidate)
    if ([IO.Path]::IsPathRooted($relative) -or
        $relative -eq '..' -or
        $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal) -or
        $relative.StartsWith("..$([IO.Path]::AltDirectorySeparatorChar)", [StringComparison]::Ordinal) -or
        [string]::Equals(
            [IO.Path]::TrimEndingDirectorySeparator($Candidate),
            [IO.Path]::TrimEndingDirectorySeparator($Root),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "$Description must be a strict child of the allowed root: $Candidate"
    }
}

function Invoke-Probe {
    param([Parameter(Mandatory)] [string[]] $Arguments)

    & $script:ResolvedProbe @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Stage scale probe exited with code $LASTEXITCODE."
    }
}

$script:ResolvedProbe = (Resolve-Path -LiteralPath $ProbePath).Path
if ([IO.Path]::GetFileName($script:ResolvedProbe) -ne 'Vok.StageScaleProbe.exe') {
    throw "ProbePath must identify Vok.StageScaleProbe.exe."
}

$resolvedAllowedRoot = (Resolve-Path -LiteralPath $AllowedOutputRoot).Path
$resolvedOutputRoot = Resolve-FullPath $OutputRoot
Assert-StrictChildPath $resolvedOutputRoot $resolvedAllowedRoot 'OutputRoot'
if (Test-Path -LiteralPath $resolvedOutputRoot) {
    throw "OutputRoot already exists: $resolvedOutputRoot"
}

$null = New-Item -ItemType Directory -Path $resolvedOutputRoot
$started = [DateTimeOffset]::UtcNow
$runs = [Collections.Generic.List[object]]::new()
$summaryPath = Join-Path $resolvedOutputRoot 'summary.json'
try {
    foreach ($count in $Counts) {
        $datasetRoot = Join-Path $resolvedOutputRoot "items-$count"
        $prepareReportPath = Join-Path $datasetRoot 'prepare.json'
        $runReportPath = Join-Path $datasetRoot 'run.json'
        Invoke-Probe @(
            'prepare',
            '--allowed-root', $resolvedOutputRoot,
            '--root', $datasetRoot,
            '--report', $prepareReportPath,
            '--source-revision', $SourceRevision,
            '--count', $count.ToString([Globalization.CultureInfo]::InvariantCulture)
        )
        $edgeSteps = if ($count -le 10000) { 0 } else { $PressureEdgeSteps }
        Invoke-Probe @(
            'run',
            '--allowed-root', $resolvedOutputRoot,
            '--root', $datasetRoot,
            '--report', $runReportPath,
            '--source-revision', $SourceRevision,
            '--count', $count.ToString([Globalization.CultureInfo]::InvariantCulture),
            '--edge-steps', $edgeSteps.ToString([Globalization.CultureInfo]::InvariantCulture),
            '--window-width', $WindowWidth.ToString([Globalization.CultureInfo]::InvariantCulture),
            '--window-height', $WindowHeight.ToString([Globalization.CultureInfo]::InvariantCulture)
        )

        $prepare = Get-Content -Raw -LiteralPath $prepareReportPath | ConvertFrom-Json
        $run = Get-Content -Raw -LiteralPath $runReportPath | ConvertFrom-Json
        if ($prepare.status -ne 'Succeeded' -or $run.status -ne 'Succeeded') {
            throw "Dataset $count did not produce successful reports."
        }

        $runs.Add([ordered]@{
            ItemCount = $count
            PrepareReport = $prepareReportPath
            RunReport = $runReportPath
            SeedMilliseconds = $prepare.seedElapsedMilliseconds
            InitializeMilliseconds = $run.viewModelInitializeMilliseconds
            FirstViewportMilliseconds = $run.firstSettledViewportMilliseconds
            Traversal = $run.traversal
            Frames = $run.frames
            Resources = $run.resources
            Display = $run.display
        })
    }

    $summary = [ordered]@{
        ReportVersion = 1
        Status = 'Succeeded'
        StartedUtc = $started.ToString('O')
        CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        SourceRevision = $SourceRevision
        ProbePath = $script:ResolvedProbe
        ProbeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $script:ResolvedProbe).Hash.ToLowerInvariant()
        OutputRoot = $resolvedOutputRoot
        Counts = $Counts
        PressureEdgeSteps = $PressureEdgeSteps
        Runs = $runs
    }
    $json = $summary | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($summaryPath, $json, [Text.UTF8Encoding]::new($false))
    $summary | ConvertTo-Json -Depth 7
}
catch {
    $failure = [ordered]@{
        ReportVersion = 1
        Status = 'Failed'
        StartedUtc = $started.ToString('O')
        FailedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        SourceRevision = $SourceRevision
        ProbePath = $script:ResolvedProbe
        OutputRoot = $resolvedOutputRoot
        Error = $_.Exception.Message
        CompletedRuns = $runs
    }
    if (-not (Test-Path -LiteralPath $summaryPath)) {
        $json = $failure | ConvertTo-Json -Depth 12
        [IO.File]::WriteAllText($summaryPath, $json, [Text.UTF8Encoding]::new($false))
    }

    throw
}
