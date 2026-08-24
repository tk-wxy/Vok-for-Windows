[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$solutionPath = Join-Path $repositoryRoot 'src\Vok.sln'
$appPath = Join-Path $repositoryRoot 'src\Vok.App\bin\Release\net10.0-windows\Vok.exe'
$appDirectory = Split-Path -Parent $appPath
$shutdownWatchdog = [TimeSpan]::FromSeconds(5)
$restartMutexName = 'Local\Vok.Restart.Script'
$restartMutex = $null
$ownsRestartMutex = $false
$exitCode = 0

function Get-RepositoryVokProcess {
    @(Get-Process -Name Vok -ErrorAction SilentlyContinue | Where-Object {
        try {
            [StringComparer]::OrdinalIgnoreCase.Equals($_.Path, $appPath)
        }
        catch {
            $false
        }
    })
}

try {
    $createdNew = $false
    $restartMutex = [Threading.Mutex]::new($true, $restartMutexName, [ref] $createdNew)
    if (-not $createdNew) {
        throw 'Another Vok start/restart operation is already running.'
    }
    $ownsRestartMutex = $true

    $runningProcesses = @(Get-RepositoryVokProcess)
    foreach ($process in $runningProcesses) {
        Write-Host "Stopping Vok process $($process.Id)..."
        Stop-Process -Id $process.Id -ErrorAction Stop
    }

    if ($runningProcesses.Count -gt 0) {
        $timer = [Diagnostics.Stopwatch]::StartNew()
        do {
            $remainingProcesses = @(Get-RepositoryVokProcess)
            if ($remainingProcesses.Count -eq 0) {
                break
            }

            if ($timer.Elapsed -ge $shutdownWatchdog) {
                $remainingIds = $remainingProcesses.Id -join ', '
                throw "Vok did not stop within $($shutdownWatchdog.TotalSeconds) seconds. Remaining PID: $remainingIds"
            }

            Start-Sleep -Milliseconds 50
        } while ($true)
    }

    Write-Host 'Building Vok (Release)...'
    Push-Location $repositoryRoot
    try {
        & dotnet build $solutionPath -c Release --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "dotnet build failed with exit code $LASTEXITCODE."
        }
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) {
        throw "Build succeeded but Vok.exe was not found: $appPath"
    }

    $startedProcess = Start-Process -FilePath $appPath -WorkingDirectory $appDirectory -PassThru
    $startedProcess.Refresh()
    if ($startedProcess.HasExited) {
        throw "Vok exited immediately with code $($startedProcess.ExitCode)."
    }

    Write-Host "Vok is running. PID: $($startedProcess.Id)"
    Write-Host 'Press Ctrl + Alt + Space to show or hide the M0 workspace.'
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)" -ForegroundColor Red
    $exitCode = 1
}
finally {
    if ($ownsRestartMutex) {
        $restartMutex.ReleaseMutex()
    }

    if ($null -ne $restartMutex) {
        $restartMutex.Dispose()
    }
}

exit $exitCode
