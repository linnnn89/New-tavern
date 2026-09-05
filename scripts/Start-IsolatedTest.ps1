#requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$StartupProbe,
    [ValidateRange(5, 120)][int]$TimeoutSeconds = 30
)

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appProject = Join-Path $projectRoot 'src/TavernDesk.App/TavernDesk.App.csproj'
$appExe = Join-Path $projectRoot 'src/TavernDesk.App/bin/Release/net10.0-windows/TavernDesk.App.exe'

# Build the source entry point; the thin release launcher does not forward args.
# No restore, package installation, release overwrite, or personal-data copying.
Push-Location $projectRoot
try {
    dotnet build $appProject -c Release --no-restore --verbosity quiet
    if ($LASTEXITCODE -ne 0) { throw '测试程序构建失败，未启动任何实例。' }
}
finally { Pop-Location }

$runRoot = Join-Path $projectRoot ('work/isolated-test-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $runRoot) { throw '测试目录必须是全新路径。' }
$startInfo = [Diagnostics.ProcessStartInfo]::new()
$startInfo.FileName = $appExe
$startInfo.WorkingDirectory = $projectRoot
$startInfo.UseShellExecute = $false
$startInfo.ArgumentList.Add('--test-root')
$startInfo.ArgumentList.Add($runRoot)
if ($StartupProbe) {
    $startInfo.ArgumentList.Add('--test-startup-probe')
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $startInfo.CreateNoWindow = $true
}
$process = [Diagnostics.Process]::Start($startInfo)
$receiptPath = Join-Path $runRoot 'startup-result.json'
$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
try {
    $receipt = $null
    do {
        if (Test-Path -LiteralPath $receiptPath) {
            $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
            break
        }
        if ($process.HasExited) { throw "测试程序提前退出，退出码 $($process.ExitCode)。" }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    if ($null -eq $receipt) { throw '等待隔离初始化超时。' }
    if ($receipt.status -notin @('initialized', 'window-shown') -or
        $receipt.processId -ne $process.Id -or $receipt.testRoot -ne $runRoot -or
        $receipt.database -ne (Join-Path $runRoot 'data/taverndesk.db') -or
        $receipt.configuration -ne (Join-Path $runRoot 'config/config.json') -or
        $receipt.logs -ne (Join-Path $runRoot 'logs') -or
        !(Test-Path -LiteralPath $receipt.database)) {
        throw "隔离启动结果校验失败，详见 $receiptPath"
    }
    if ($StartupProbe) {
        $remainingMs = [Math]::Max(1, [int]($deadline - [DateTime]::UtcNow).TotalMilliseconds)
        if (!$process.WaitForExit($remainingMs)) { throw '初始化已完成，但测试进程退出超时。' }
        if ($process.ExitCode -ne 0) { throw "测试初始化失败，退出码 $($process.ExitCode)。" }
    }
    [pscustomobject]@{
        Mode = $(if ($StartupProbe) { 'InitializationProbe' } else { 'InteractiveTest' })
        ProcessId = $process.Id
        Executable = $appExe
        TestRoot = $runRoot
        Receipt = $receiptPath
        Status = $receipt.status
        Exited = $process.HasExited
    }
}
catch {
    # Only terminate the process created by this invocation. Preserve evidence.
    if (!$process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    throw
}
finally { $process.Dispose() }
