[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$harness = Join-Path $PSScriptRoot 'GroupContextHistoryTdd\GroupContextHistoryTdd.csproj'

Push-Location $projectRoot
try {
    dotnet run --project $harness --configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "群聊长上下文离线测试失败，退出码：$LASTEXITCODE"
    }
}
finally {
    Pop-Location
}
