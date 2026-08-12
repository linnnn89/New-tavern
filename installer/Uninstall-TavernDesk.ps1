param(
    [switch]$Quiet,
    [string]$CleanupTarget,
    [int]$WaitForProcessId = 0
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms

$productName = 'TavernDesk'
$markerFileName = '.taverndesk-install.json'
$textByCulture = @{
    'zh-CN' = @{
        Title = '卸载 TavernDesk'
        Confirm = "是否删除 TavernDesk 程序文件和快捷方式？`r`n`r`n角色、聊天、设置和 API Key 等个人资料不会被删除。"
        Success = 'TavernDesk 程序文件已删除，个人资料保持不变。'
        Failure = '卸载失败：{0}'
    }
    'zh-TW' = @{
        Title = '解除安裝 TavernDesk'
        Confirm = "是否刪除 TavernDesk 程式檔案與捷徑？`r`n`r`n角色、對話、設定和 API Key 等個人資料不會被刪除。"
        Success = 'TavernDesk 程式檔案已刪除，個人資料保持不變。'
        Failure = '解除安裝失敗：{0}'
    }
    'en-US' = @{
        Title = 'Uninstall TavernDesk'
        Confirm = "Remove TavernDesk program files and shortcuts?`r`n`r`nCharacters, chats, settings, API keys, and other personal data will not be deleted."
        Success = 'TavernDesk program files were removed. Personal data was left unchanged.'
        Failure = 'Uninstall failed: {0}'
    }
    'ja-JP' = @{
        Title = 'TavernDesk のアンインストール'
        Confirm = "TavernDesk のプログラムファイルとショートカットを削除しますか？`r`n`r`nキャラクター、チャット、設定、API Key などの個人データは削除されません。"
        Success = 'TavernDesk のプログラムファイルを削除しました。個人データは変更されていません。'
        Failure = 'アンインストールに失敗しました：{0}'
    }
}

function Get-DesktopFolder {
    if (-not [string]::IsNullOrWhiteSpace($env:TAVERNDESK_INSTALLER_TEST_DESKTOP)) {
        return [IO.Path]::GetFullPath($env:TAVERNDESK_INSTALLER_TEST_DESKTOP)
    }
    return [Environment]::GetFolderPath('Desktop')
}

function Get-ProgramsFolder {
    if (-not [string]::IsNullOrWhiteSpace($env:TAVERNDESK_INSTALLER_TEST_PROGRAMS)) {
        return [IO.Path]::GetFullPath($env:TAVERNDESK_INSTALLER_TEST_PROGRAMS)
    }
    return [Environment]::GetFolderPath('Programs')
}

function Read-Marker {
    param([string]$TargetPath)

    $fullPath = [IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
    $rootPath = [IO.Path]::GetPathRoot($fullPath).TrimEnd('\')
    if ([string]::Equals($fullPath, $rootPath, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unsafe uninstall target.'
    }
    $markerPath = Join-Path $fullPath $markerFileName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) {
        throw 'The TavernDesk install marker is missing.'
    }
    $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($marker.product -ne $productName) {
        throw 'The install marker does not belong to TavernDesk.'
    }
    return $marker
}

function Get-Culture {
    param($Marker)
    $culture = [string]$Marker.installerLanguage
    if ($textByCulture.ContainsKey($culture)) { return $culture }
    return 'en-US'
}

function Remove-Shortcuts {
    param($Marker)

    if ([bool]$Marker.desktopShortcut) {
        $desktopLink = Join-Path (Get-DesktopFolder) 'TavernDesk.lnk'
        if (Test-Path -LiteralPath $desktopLink -PathType Leaf) {
            Remove-Item -LiteralPath $desktopLink -Force
        }
    }
    if ([bool]$Marker.startMenuShortcut) {
        $programsFolder = [IO.Path]::GetFullPath((Get-ProgramsFolder)).TrimEnd('\')
        $startMenuPath = Join-Path $programsFolder 'TavernDesk'
        $actualParent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($startMenuPath)).TrimEnd('\')
        if (-not [string]::Equals($programsFolder, $actualParent, [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Unsafe Start menu path.'
        }
        if (Test-Path -LiteralPath $startMenuPath -PathType Container) {
            Remove-Item -LiteralPath $startMenuPath -Recurse -Force
        }
    }
}

if (-not [string]::IsNullOrWhiteSpace($CleanupTarget)) {
    $targetPath = [IO.Path]::GetFullPath($CleanupTarget).TrimEnd('\')
    $marker = Read-Marker $targetPath
    $culture = Get-Culture $marker
    if ($WaitForProcessId -gt 0) {
        for ($index = 0; $index -lt 100; $index++) {
            if ($null -eq (Get-Process -Id $WaitForProcessId -ErrorAction SilentlyContinue)) { break }
            Start-Sleep -Milliseconds 100
        }
    }
    Set-Location ([IO.Path]::GetTempPath())
    Remove-Item -LiteralPath $targetPath -Recurse -Force
    if (-not $Quiet) {
        [System.Windows.Forms.MessageBox]::Show(
            $textByCulture[$culture].Success,
            $textByCulture[$culture].Title,
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    }
    try { Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue } catch { }
    exit 0
}

$installPath = [IO.Path]::GetFullPath($PSScriptRoot).TrimEnd('\')
$marker = $null
try {
    $marker = Read-Marker $installPath
    $culture = Get-Culture $marker
    if (-not $Quiet) {
        $answer = [System.Windows.Forms.MessageBox]::Show(
            $textByCulture[$culture].Confirm,
            $textByCulture[$culture].Title,
            [System.Windows.Forms.MessageBoxButtons]::YesNo,
            [System.Windows.Forms.MessageBoxIcon]::Question,
            [System.Windows.Forms.MessageBoxDefaultButton]::Button2)
        if ($answer -ne [System.Windows.Forms.DialogResult]::Yes) { exit 2 }
    }

    foreach ($process in @(Get-Process -Name 'TavernDesk.App' -ErrorAction SilentlyContinue)) {
        try {
            if ($process.Path -and $process.Path.StartsWith(
                    $installPath + '\',
                    [StringComparison]::OrdinalIgnoreCase)) {
                $process.CloseMainWindow() | Out-Null
                if (-not $process.WaitForExit(5000)) {
                    throw 'TavernDesk is still running.'
                }
            }
        }
        catch {
            throw
        }
    }

    Remove-Shortcuts $marker
    $cleanupScript = Join-Path ([IO.Path]::GetTempPath()) ('TavernDesk-Uninstall-' + [Guid]::NewGuid().ToString('N') + '.ps1')
    Copy-Item -LiteralPath $PSCommandPath -Destination $cleanupScript
    $quietArgument = if ($Quiet) { ' -Quiet' } else { '' }
    $argumentLine = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File "' `
        + $cleanupScript + '" -CleanupTarget "' + $installPath `
        + '" -WaitForProcessId ' + $PID + $quietArgument
    Start-Process -FilePath 'powershell.exe' -ArgumentList $argumentLine
    exit 0
}
catch {
    $culture = if ($null -ne $marker) { Get-Culture $marker } else { 'en-US' }
    if (-not $Quiet) {
        [System.Windows.Forms.MessageBox]::Show(
            ([string]::Format($textByCulture[$culture].Failure, $_.Exception.Message)),
            $textByCulture[$culture].Title,
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
    exit 1
}
