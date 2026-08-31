[CmdletBinding()]
param(
    [switch]$SelfTest,

    [switch]$Enumerate,

    [ValidateRange(1, 100)]
    [int]$Count = 1,

    [ValidateRange(1, 3600)]
    [int]$TimeoutSeconds = 300,

    [string]$ExpectedPath,

    [switch]$IncludePaths
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($SelfTest -and $Enumerate) {
    throw 'SelfTest and Enumerate cannot be combined.'
}

$project = Join-Path $PSScriptRoot 'Vok.ImageDestinationProbe\Vok.ImageDestinationProbe.csproj'
$probeArtifacts = Join-Path `
    $env:TEMP `
    ('vok-m2-image-destination-probe-{0}-{1}' -f $PID, [guid]::NewGuid().ToString('N'))
$tempRoot = [System.IO.Path]::GetFullPath($env:TEMP).TrimEnd(
    [System.IO.Path]::DirectorySeparatorChar,
    [System.IO.Path]::AltDirectorySeparatorChar)
$probeArtifacts = [System.IO.Path]::GetFullPath($probeArtifacts)
$requiredPrefix = $tempRoot + [System.IO.Path]::DirectorySeparatorChar
if (-not $probeArtifacts.StartsWith(
        $requiredPrefix,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The probe artifacts directory is outside the process temporary directory.'
}

$exitCode = 0
try {
    dotnet build $project -c Release --artifacts-path $probeArtifacts
    $exitCode = $LASTEXITCODE
    if ($exitCode -eq 0) {

        $probeAssembly = Join-Path $probeArtifacts 'bin\Vok.ImageDestinationProbe\release\Vok.ImageDestinationProbe.dll'
        if (-not (Test-Path -LiteralPath $probeAssembly)) {
            throw "Image destination probe assembly was not produced: $probeAssembly"
        }

        $probeArguments = @(
            $probeAssembly,
            '--count', $Count.ToString([System.Globalization.CultureInfo]::InvariantCulture),
            '--timeout-seconds', $TimeoutSeconds.ToString([System.Globalization.CultureInfo]::InvariantCulture)
        )
        if ($SelfTest) {
            $probeArguments += '--self-test'
        }
        elseif ($Enumerate) {
            $probeArguments += '--enumerate'
        }
        if (-not [string]::IsNullOrWhiteSpace($ExpectedPath)) {
            $probeArguments += @('--expected-path', $ExpectedPath)
        }
        if ($IncludePaths) {
            $probeArguments += '--include-paths'
        }

        dotnet @probeArguments
        $exitCode = $LASTEXITCODE
    }
}
finally {
    if (Test-Path -LiteralPath $probeArtifacts) {
        Remove-Item -LiteralPath $probeArtifacts -Recurse -Force
    }
}

exit $exitCode
