[CmdletBinding()]
param(
    [string] $AppPath,
    [ValidateSet('default', 'enabled', 'disabled')] [string] $PrecreateHwnd = 'default'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if (-not $AppPath) {
    $AppPath = Join-Path $PSScriptRoot '..\src\Vok.App\bin\Release\net10.0-windows\Vok.exe'
}

# The smoke probe intentionally reuses the same structured generation/five-gate
# contract as the performance probe. It never infers WPF focus from Win32 state.
& (Join-Path $PSScriptRoot 'Measure-HostInteraction.ps1') `
    -Label 'Vok M0 smoke' `
    -AppPath $AppPath `
    -Gesture control-alt-space `
    -Cycles 2 `
    -WarmupMilliseconds 500 `
    -ResourceSampleEvery 1 `
    -HostDiagnostics required `
    -PrecreateHwnd $PrecreateHwnd
