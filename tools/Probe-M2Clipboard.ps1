[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Vok.ClipboardProbe\Vok.ClipboardProbe.csproj'
$probeArtifacts = Join-Path $env:TEMP 'vok-m2-clipboard-probe'

dotnet build $project -c Release --artifacts-path $probeArtifacts
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

$probeAssembly = Join-Path $probeArtifacts 'bin\Vok.ClipboardProbe\release\Vok.ClipboardProbe.dll'
if (-not (Test-Path -LiteralPath $probeAssembly)) {
    throw "Clipboard probe assembly was not produced: $probeAssembly"
}

dotnet $probeAssembly
exit $LASTEXITCODE
