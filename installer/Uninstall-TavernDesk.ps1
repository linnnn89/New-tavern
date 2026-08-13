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
$managedManifestFileName = '.taverndesk-managed-files.json'
$textByCulture = @{
    'zh-CN' = @{
        Title = '卸载 TavernDesk'
        Confirm = "是否删除 TavernDesk 程序文件、快捷方式和安装目录内的 API 测试输出？`r`n`r`n角色、聊天、设置和 API Key 等个人资料不会被删除。"
        Success = 'TavernDesk 程序文件和 API 测试输出已删除；安装目录中的其他文件以及个人资料保持不变。'
        Failure = '卸载失败：{0}'
    }
    'zh-TW' = @{
        Title = '解除安裝 TavernDesk'
        Confirm = "是否刪除 TavernDesk 程式檔案、捷徑及安裝目錄內的 API 測試輸出？`r`n`r`n角色、對話、設定和 API Key 等個人資料不會被刪除。"
        Success = 'TavernDesk 程式檔案和 API 測試輸出已刪除；安裝目錄中的其他檔案與個人資料保持不變。'
        Failure = '解除安裝失敗：{0}'
    }
    'en-US' = @{
        Title = 'Uninstall TavernDesk'
        Confirm = "Remove TavernDesk program files, shortcuts, and API test output stored inside the install folder?`r`n`r`nCharacters, chats, settings, API keys, and other personal data will not be deleted."
        Success = 'TavernDesk program files and API test output were removed. Other files in the install folder and personal data were left unchanged.'
        Failure = 'Uninstall failed: {0}'
    }
    'ja-JP' = @{
        Title = 'TavernDesk のアンインストール'
        Confirm = "TavernDesk のプログラムファイル、ショートカット、インストールフォルダー内の API テスト出力を削除しますか？`r`n`r`nキャラクター、チャット、設定、API Key などの個人データは削除されません。"
        Success = 'TavernDesk のプログラムファイルと API テスト出力を削除しました。インストール先のその他のファイルと個人データは変更されていません。'
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

function Resolve-InstallChildPath {
    param([string]$RootPath, [string]$RelativePath)

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw 'The managed-file manifest contains an unsafe path.'
    }
    $root = [IO.Path]::GetFullPath($RootPath).TrimEnd('\')
    $candidate = [IO.Path]::GetFullPath((Join-Path $root $RelativePath.Replace('/', '\')))
    if (-not $candidate.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The managed-file manifest contains a path outside the installation directory.'
    }
    return $candidate
}

function Read-ManagedFiles {
    param([string]$RootPath)

    $manifestPath = Join-Path $RootPath $managedManifestFileName
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw 'The managed-file manifest is missing; the uninstaller will not delete the installation directory blindly.'
    }
    $manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ($manifest.product -ne $productName -or [int]$manifest.schemaVersion -ne 1) {
        throw 'The managed-file manifest is invalid.'
    }
    return @($manifest.files | ForEach-Object {
        Resolve-InstallChildPath $RootPath ([string]$_)
    })
}

function Remove-EmptyInstallDirectories {
    param([string]$RootPath)

    $directories = @(Get-ChildItem -LiteralPath $RootPath -Recurse -Force -Directory `
        | Sort-Object { $_.FullName.Length } -Descending)
    foreach ($directory in $directories) {
        if (@(Get-ChildItem -LiteralPath $directory.FullName -Force).Count -eq 0) {
            [IO.Directory]::Delete($directory.FullName)
        }
    }
}

function Remove-ManagedInstallContent {
    param([string]$TargetPath)

    $root = [IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
    $manifestPath = Join-Path $root $managedManifestFileName
    $markerPath = Join-Path $root $markerFileName
    $managedFiles = Read-ManagedFiles $root

    foreach ($filePath in @($managedFiles | Sort-Object { $_.Length } -Descending)) {
        if ([string]::Equals($filePath, $manifestPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
        if (Test-Path -LiteralPath $filePath -PathType Leaf) {
            Remove-Item -LiteralPath $filePath -Force
        }
    }

    $testOutputPath = Resolve-InstallChildPath $root 'tests\output'
    if (Test-Path -LiteralPath $testOutputPath -PathType Container) {
        Remove-Item -LiteralPath $testOutputPath -Recurse -Force
    }
    $uninstallCommand = Resolve-InstallChildPath $root 'Uninstall TavernDesk.cmd'
    if (Test-Path -LiteralPath $uninstallCommand -PathType Leaf) {
        Remove-Item -LiteralPath $uninstallCommand -Force
    }

    Remove-EmptyInstallDirectories $root
    Remove-Item -LiteralPath $manifestPath -Force
    Remove-Item -LiteralPath $markerPath -Force
    Remove-EmptyInstallDirectories $root
    if (@(Get-ChildItem -LiteralPath $root -Force).Count -eq 0) {
        [IO.Directory]::Delete($root)
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
    Remove-ManagedInstallContent $targetPath
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
