[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ProbePath,
    [Parameter(Mandatory)] [string] $AllowedOutputRoot,
    [Parameter(Mandatory)] [string] $OutputRoot,
    [Parameter(Mandatory)] [string] $SourceRevision,
    [ValidateRange(1, 100000)] [int] $Count = 10000,
    [ValidateRange(0, 5000)] [int] $WarmupEdgeSteps = 0,
    [switch] $CloseAfterWarmup
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
    throw 'ProbePath must identify Vok.StageScaleProbe.exe.'
}

$resolvedAllowedRoot = (Resolve-Path -LiteralPath $AllowedOutputRoot).Path
$resolvedOutputRoot = Resolve-FullPath $OutputRoot
Assert-StrictChildPath $resolvedOutputRoot $resolvedAllowedRoot 'OutputRoot'
if (Test-Path -LiteralPath $resolvedOutputRoot) {
    throw "OutputRoot already exists: $resolvedOutputRoot"
}

$null = New-Item -ItemType Directory -Path $resolvedOutputRoot
$started = [DateTimeOffset]::UtcNow
$datasetRoot = Join-Path $resolvedOutputRoot "items-$Count"
$prepareReportPath = Join-Path $datasetRoot 'prepare.json'
$inspectionReportPath = Join-Path $datasetRoot 'inspection.json'
$summaryPath = Join-Path $resolvedOutputRoot 'inspection-summary.json'
try {
    Invoke-Probe @(
        'prepare',
        '--allowed-root', $resolvedOutputRoot,
        '--root', $datasetRoot,
        '--report', $prepareReportPath,
        '--source-revision', $SourceRevision,
        '--count', $Count.ToString([Globalization.CultureInfo]::InvariantCulture)
    )
    Invoke-Probe @(
        'inspect',
        '--allowed-root', $resolvedOutputRoot,
        '--root', $datasetRoot,
        '--report', $inspectionReportPath,
        '--source-revision', $SourceRevision,
        '--count', $Count.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--warmup-edge-steps', $WarmupEdgeSteps.ToString([Globalization.CultureInfo]::InvariantCulture),
        '--close-after-warmup', $CloseAfterWarmup.IsPresent.ToString().ToLowerInvariant()
    )

    $prepare = Get-Content -Raw -LiteralPath $prepareReportPath | ConvertFrom-Json
    $inspection = Get-Content -Raw -LiteralPath $inspectionReportPath | ConvertFrom-Json
    if ($prepare.status -ne 'Succeeded' -or $inspection.status -ne 'Recorded') {
        throw 'The physical inspection did not produce complete prepare and inspection reports.'
    }

    $summary = [ordered]@{
        ReportVersion = 1
        Status = 'Recorded'
        StartedUtc = $started.ToString('O')
        CompletedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        SourceRevision = $SourceRevision
        ProbePath = $script:ResolvedProbe
        ProbeSha256 = (Get-FileHash -Algorithm SHA256 -LiteralPath $script:ResolvedProbe).Hash.ToLowerInvariant()
        OutputRoot = $resolvedOutputRoot
        ItemCount = $Count
        WarmupEdgeSteps = $WarmupEdgeSteps
        CloseAfterWarmup = $CloseAfterWarmup.IsPresent
        PrepareReport = $prepareReportPath
        InspectionReport = $inspectionReportPath
        Display = $inspection.display
        Inspection = $inspection.inspection
        Resources = $inspection.resources
    }
    $json = $summary | ConvertTo-Json -Depth 12
    [IO.File]::WriteAllText($summaryPath, $json, [Text.UTF8Encoding]::new($false))
    $summary | ConvertTo-Json -Depth 8
}
catch {
    if (-not (Test-Path -LiteralPath $summaryPath)) {
        $failure = [ordered]@{
            ReportVersion = 1
            Status = 'Failed'
            StartedUtc = $started.ToString('O')
            FailedUtc = [DateTimeOffset]::UtcNow.ToString('O')
            SourceRevision = $SourceRevision
            ProbePath = $script:ResolvedProbe
            OutputRoot = $resolvedOutputRoot
            Error = $_.Exception.Message
        }
        $json = $failure | ConvertTo-Json -Depth 12
        [IO.File]::WriteAllText($summaryPath, $json, [Text.UTF8Encoding]::new($false))
    }

    throw
}
