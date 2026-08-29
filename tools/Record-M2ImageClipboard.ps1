[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int]$Count = 6,

    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 300,

    [string[]]$Labels = @(
        'snipping-rectangle',
        'print-screen',
        'browser-image',
        'office-image',
        'transparent-png',
        'chat-image'
    ),

    [string]$OutputPath,

    [switch]$SelfTest,

    [switch]$PrepareSamples,

    [switch]$IncludeNonImage
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function ConvertTo-RecorderLabels {
    param([string[]]$Values)

    foreach ($value in $Values) {
        foreach ($piece in $value -split ',') {
            $trimmed = $piece.Trim()
            if (-not [string]::IsNullOrWhiteSpace($trimmed)) {
                $trimmed
            }
        }
    }
}

function Resolve-RecorderCaptureCount {
    param(
        [int]$RequestedCount,
        [string[]]$NormalizedLabels,
        [bool]$CountWasSpecified
    )

    if (-not $CountWasSpecified -and $NormalizedLabels.Count -gt 0) {
        return $NormalizedLabels.Count
    }

    return $RequestedCount
}

$labelsWereSpecified = $PSBoundParameters.ContainsKey('Labels')
$countWasSpecified = $PSBoundParameters.ContainsKey('Count')
$Labels = @(ConvertTo-RecorderLabels -Values $Labels)
if ($labelsWereSpecified -and $Labels.Count -eq 0) {
    throw 'Labels must contain at least one non-empty value.'
}
$Count = Resolve-RecorderCaptureCount `
    -RequestedCount $Count `
    -NormalizedLabels $Labels `
    -CountWasSpecified $countWasSpecified

$project = Join-Path $PSScriptRoot 'Vok.ClipboardImageRecorder\Vok.ClipboardImageRecorder.csproj'
$recorderArtifacts = Join-Path $env:TEMP 'vok-m2-image-recorder'

dotnet build $project -c Release --artifacts-path $recorderArtifacts
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$recorderAssembly = Join-Path $recorderArtifacts 'bin\Vok.ClipboardImageRecorder\release\Vok.ClipboardImageRecorder.dll'
if (-not (Test-Path -LiteralPath $recorderAssembly)) {
    throw "Clipboard image recorder assembly was not produced: $recorderAssembly"
}

if ($SelfTest) {
    $commaLabels = @(ConvertTo-RecorderLabels -Values @('a,b,c'))
    $arrayLabels = @(ConvertTo-RecorderLabels -Values @('a', ' b ', '', 'c'))
    if (($commaLabels -join '|') -ne 'a|b|c' -or
        ($arrayLabels -join '|') -ne 'a|b|c' -or
        (Resolve-RecorderCaptureCount 6 $commaLabels $false) -ne 3 -or
        (Resolve-RecorderCaptureCount 2 $commaLabels $true) -ne 2) {
        throw 'Clipboard image recorder wrapper self-test failed.'
    }

    dotnet $recorderAssembly --self-test
    exit $LASTEXITCODE
}

if ($PrepareSamples) {
    dotnet $recorderAssembly --prepare-samples
    exit $LASTEXITCODE
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $recordingDirectory = Join-Path $env:TEMP 'Vok.ClipboardImageRecorder'
    $recordingName = 'm2.3-image-formats-{0}.jsonl' -f (Get-Date -Format 'yyyyMMdd-HHmmss-fff')
    $OutputPath = Join-Path $recordingDirectory $recordingName
}

$recorderArguments = @(
    $recorderAssembly,
    '--output', $OutputPath,
    '--count', $Count.ToString([System.Globalization.CultureInfo]::InvariantCulture),
    '--timeout-seconds', $TimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)
)
if ($Labels.Count -gt 0) {
    $recorderArguments += @('--labels', ($Labels -join ','))
}
if ($IncludeNonImage) {
    $recorderArguments += '--include-non-image'
}

dotnet @recorderArguments
exit $LASTEXITCODE
