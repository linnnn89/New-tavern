param(
    [switch]$Quiet,
    [string]$InstallPath,
    [ValidateSet('zh-CN', 'zh-TW', 'en-US', 'ja-JP')]
    [string]$Language,
    [switch]$NoDesktopShortcut,
    [switch]$NoStartMenuShortcut,
    [switch]$NoLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[System.Windows.Forms.Application]::EnableVisualStyles()

$script:ProductName = 'TavernDesk'
$script:MarkerFileName = '.taverndesk-install.json'
$script:PayloadArchive = Join-Path $PSScriptRoot 'payload.zip'

$script:TextByCulture = @{
    'zh-CN' = @{
        LanguageTitle = '选择安装界面语言'
        LanguageHeading = 'TavernDesk 安装程序'
        LanguagePrompt = '请选择安装向导使用的语言。'
        Continue = '继续'
        Cancel = '取消'
        SetupTitle = '安装 TavernDesk'
        Heading = '为当前用户安装 TavernDesk'
        Intro = '这是面向玩家的纯净版本，只安装程序文件，不包含角色、聊天、API Key、个人设置或开发工具。'
        RuntimePresent = '已检测到系统 .NET 10。TavernDesk 仍使用安装目录中的私有运行时，不修改系统 .NET。'
        RuntimeMissing = '未检测到系统 .NET 10。安装包会自动部署私有 .NET 10、SQLite、Tokenizer 和全部运行依赖，不需要另行安装。'
        RegistryNote = '安装程序不会创建注册表项；因此不会出现在 Windows“已安装的应用”列表中。可从开始菜单运行卸载程序。'
        InstallLocation = '安装位置'
        Browse = '浏览…'
        BrowseDescription = '选择 TavernDesk 程序安装目录'
        DesktopShortcut = '创建桌面快捷方式'
        StartMenuShortcut = '创建开始菜单快捷方式和卸载入口'
        LaunchAfter = '安装完成后启动 TavernDesk'
        Install = '安装'
        Installing = '正在安装 TavernDesk，请稍候…'
        Success = 'TavernDesk 已安装完成。首次运行时可选择应用界面语言。'
        Failure = '安装失败：{0}'
        InvalidPath = '请输入有效的安装路径。'
        UnsafePath = '不能把磁盘根目录、Windows 目录、用户主目录或系统目录本身作为安装位置。请选择其下的 TavernDesk 子目录。'
        UnknownDirectory = '目标目录不是空目录，也不是由本安装程序管理的 TavernDesk 安装目录。为避免覆盖其他文件，安装已停止。'
        DevelopmentDirectory = '目标目录看起来是 TavernDesk 源码或开发工作区，安装程序不会覆盖它。'
        NotWritable = '无法写入所选目录。请选择当前用户可写的位置，或用适当权限重新运行安装程序。'
        AppRunning = '目标目录中的 TavernDesk 正在运行。请先关闭它，再重新安装。'
        PayloadMissing = '安装包缺少应用文件，请重新下载安装程序。'
        ShortcutFailure = '无法创建快捷方式：{0}'
    }
    'zh-TW' = @{
        LanguageTitle = '選擇安裝介面語言'
        LanguageHeading = 'TavernDesk 安裝程式'
        LanguagePrompt = '請選擇安裝精靈使用的語言。'
        Continue = '繼續'
        Cancel = '取消'
        SetupTitle = '安裝 TavernDesk'
        Heading = '為目前使用者安裝 TavernDesk'
        Intro = '這是提供玩家使用的純淨版本，只安裝程式檔案，不包含角色、對話、API Key、個人設定或開發工具。'
        RuntimePresent = '已偵測到系統 .NET 10。TavernDesk 仍使用安裝目錄中的私有執行階段，不修改系統 .NET。'
        RuntimeMissing = '未偵測到系統 .NET 10。安裝包會自動部署私有 .NET 10、SQLite、Tokenizer 與全部執行依賴，不需要另外安裝。'
        RegistryNote = '安裝程式不會建立登錄項目；因此不會出現在 Windows「已安裝的應用程式」清單。可從開始功能表執行解除安裝程式。'
        InstallLocation = '安裝位置'
        Browse = '瀏覽…'
        BrowseDescription = '選擇 TavernDesk 程式安裝目錄'
        DesktopShortcut = '建立桌面捷徑'
        StartMenuShortcut = '建立開始功能表捷徑與解除安裝入口'
        LaunchAfter = '安裝完成後啟動 TavernDesk'
        Install = '安裝'
        Installing = '正在安裝 TavernDesk，請稍候…'
        Success = 'TavernDesk 已安裝完成。首次執行時可選擇應用程式介面語言。'
        Failure = '安裝失敗：{0}'
        InvalidPath = '請輸入有效的安裝路徑。'
        UnsafePath = '不能將磁碟根目錄、Windows 目錄、使用者主目錄或系統目錄本身設為安裝位置。請選擇其下的 TavernDesk 子目錄。'
        UnknownDirectory = '目標目錄不是空目錄，也不是由本安裝程式管理的 TavernDesk 安裝目錄。為避免覆寫其他檔案，安裝已停止。'
        DevelopmentDirectory = '目標目錄看起來是 TavernDesk 原始碼或開發工作區，安裝程式不會覆寫它。'
        NotWritable = '無法寫入所選目錄。請選擇目前使用者可寫入的位置，或以適當權限重新執行安裝程式。'
        AppRunning = '目標目錄中的 TavernDesk 正在執行。請先關閉它，再重新安裝。'
        PayloadMissing = '安裝包缺少應用程式檔案，請重新下載安裝程式。'
        ShortcutFailure = '無法建立捷徑：{0}'
    }
    'en-US' = @{
        LanguageTitle = 'Choose setup language'
        LanguageHeading = 'TavernDesk Setup'
        LanguagePrompt = 'Choose the language used by the setup wizard.'
        Continue = 'Continue'
        Cancel = 'Cancel'
        SetupTitle = 'Install TavernDesk'
        Heading = 'Install TavernDesk for the current user'
        Intro = 'This clean player build installs program files only. It contains no characters, chats, API keys, personal settings, or development tools.'
        RuntimePresent = 'System .NET 10 was detected. TavernDesk still uses its private runtime in the install directory and does not modify system .NET.'
        RuntimeMissing = 'System .NET 10 was not detected. Setup deploys a private .NET 10 runtime, SQLite, tokenizers, and all required dependencies automatically.'
        RegistryNote = 'Setup creates no registry entries, so TavernDesk will not appear in Windows Installed apps. Use the Start menu uninstall entry instead.'
        InstallLocation = 'Install location'
        Browse = 'Browse…'
        BrowseDescription = 'Choose the TavernDesk program installation folder'
        DesktopShortcut = 'Create a desktop shortcut'
        StartMenuShortcut = 'Create Start menu shortcuts and an uninstall entry'
        LaunchAfter = 'Launch TavernDesk after setup'
        Install = 'Install'
        Installing = 'Installing TavernDesk. Please wait…'
        Success = 'TavernDesk was installed successfully. You can choose the application language on first launch.'
        Failure = 'Setup failed: {0}'
        InvalidPath = 'Enter a valid installation path.'
        UnsafePath = 'A drive root, the Windows directory, the user profile root, or a system directory itself cannot be used. Choose a TavernDesk subfolder instead.'
        UnknownDirectory = 'The destination is neither empty nor a TavernDesk installation managed by this setup. Setup stopped to avoid overwriting unrelated files.'
        DevelopmentDirectory = 'The destination appears to be a TavernDesk source or development workspace. Setup will not overwrite it.'
        NotWritable = 'The selected location is not writable. Choose a per-user location or run setup with suitable permissions.'
        AppRunning = 'TavernDesk is running from the selected destination. Close it before installing again.'
        PayloadMissing = 'Application files are missing from this setup package. Download the installer again.'
        ShortcutFailure = 'A shortcut could not be created: {0}'
    }
    'ja-JP' = @{
        LanguageTitle = 'セットアップ言語の選択'
        LanguageHeading = 'TavernDesk セットアップ'
        LanguagePrompt = 'セットアップウィザードで使用する言語を選択してください。'
        Continue = '続行'
        Cancel = 'キャンセル'
        SetupTitle = 'TavernDesk のインストール'
        Heading = '現在のユーザー用に TavernDesk をインストール'
        Intro = 'プレイヤー向けのクリーン版です。プログラムファイルのみをインストールし、キャラクター、チャット、API Key、個人設定、開発ツールは含みません。'
        RuntimePresent = 'システムの .NET 10 を検出しました。TavernDesk はインストール先のプライベートランタイムを使用し、システムの .NET は変更しません。'
        RuntimeMissing = 'システムの .NET 10 は見つかりませんでした。プライベート .NET 10、SQLite、Tokenizer、および必要な依存関係を自動的に配置します。'
        RegistryNote = 'レジストリエントリは作成しないため、Windows の「インストールされているアプリ」には表示されません。スタートメニューのアンインストール項目を使用してください。'
        InstallLocation = 'インストール先'
        Browse = '参照…'
        BrowseDescription = 'TavernDesk のインストール先フォルダーを選択'
        DesktopShortcut = 'デスクトップにショートカットを作成'
        StartMenuShortcut = 'スタートメニューにショートカットとアンインストール項目を作成'
        LaunchAfter = '完了後に TavernDesk を起動'
        Install = 'インストール'
        Installing = 'TavernDesk をインストールしています。お待ちください…'
        Success = 'TavernDesk のインストールが完了しました。初回起動時にアプリの表示言語を選択できます。'
        Failure = 'インストールに失敗しました：{0}'
        InvalidPath = '有効なインストール先を入力してください。'
        UnsafePath = 'ドライブのルート、Windows フォルダー、ユーザープロファイルのルート、またはシステムフォルダー自体は指定できません。TavernDesk 用のサブフォルダーを選択してください。'
        UnknownDirectory = '対象フォルダーは空ではなく、このセットアップが管理する TavernDesk インストールでもありません。無関係なファイルを上書きしないよう中止しました。'
        DevelopmentDirectory = '対象フォルダーは TavernDesk のソースまたは開発ワークスペースです。セットアップは上書きしません。'
        NotWritable = '選択した場所に書き込めません。ユーザーが書き込める場所を選ぶか、適切な権限で再実行してください。'
        AppRunning = '選択した場所の TavernDesk が実行中です。終了してから再度インストールしてください。'
        PayloadMissing = 'セットアップパッケージにアプリケーションファイルがありません。インストーラーを再度ダウンロードしてください。'
        ShortcutFailure = 'ショートカットを作成できませんでした：{0}'
    }
}

function Normalize-CultureName {
    param([string]$Requested)

    if ([string]::IsNullOrWhiteSpace($Requested)) {
        $Requested = [System.Globalization.CultureInfo]::CurrentUICulture.Name
    }

    if ($Requested -like 'zh-TW*' -or $Requested -like 'zh-Hant*') { return 'zh-TW' }
    if ($Requested -like 'zh*') { return 'zh-CN' }
    if ($Requested -like 'ja*') { return 'ja-JP' }
    return 'en-US'
}

function Get-Text {
    param([string]$Culture, [string]$Key)
    return [string]$script:TextByCulture[$Culture][$Key]
}

function Show-ErrorMessage {
    param([string]$Culture, [string]$Message)
    if (-not $Quiet) {
        [System.Windows.Forms.MessageBox]::Show(
            $Message,
            (Get-Text $Culture 'SetupTitle'),
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
    }
}

function Show-LanguagePicker {
    $form = New-Object System.Windows.Forms.Form
    $form.Text = 'TavernDesk Setup'
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.ClientSize = New-Object System.Drawing.Size(500, 235)
    $form.Font = New-Object System.Drawing.Font('Segoe UI', 10)
    $form.AutoScaleMode = 'Dpi'

    $heading = New-Object System.Windows.Forms.Label
    $heading.Text = 'TavernDesk Setup · 安装程序 · 安裝程式 · セットアップ'
    $heading.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 16)
    $heading.AutoSize = $true
    $heading.Location = New-Object System.Drawing.Point(28, 25)
    $form.Controls.Add($heading)

    $prompt = New-Object System.Windows.Forms.Label
    $prompt.Text = 'Choose setup language / 选择安装语言 / 選擇安裝語言 / 言語を選択'
    $prompt.AutoSize = $true
    $prompt.Location = New-Object System.Drawing.Point(30, 78)
    $form.Controls.Add($prompt)

    $combo = New-Object System.Windows.Forms.ComboBox
    $combo.DropDownStyle = 'DropDownList'
    $combo.Location = New-Object System.Drawing.Point(33, 108)
    $combo.Size = New-Object System.Drawing.Size(434, 32)
    $choices = @(
        [pscustomobject]@{ Code = 'zh-CN'; Label = '简体中文' },
        [pscustomobject]@{ Code = 'zh-TW'; Label = '繁體中文' },
        [pscustomobject]@{ Code = 'en-US'; Label = 'English' },
        [pscustomobject]@{ Code = 'ja-JP'; Label = '日本語' }
    )
    [void]$combo.Items.AddRange($choices)
    $combo.DisplayMember = 'Label'
    $preferred = Normalize-CultureName ([System.Globalization.CultureInfo]::CurrentUICulture.Name)
    $combo.SelectedIndex = 0
    for ($index = 0; $index -lt $choices.Count; $index++) {
        if ($choices[$index].Code -eq $preferred) {
            $combo.SelectedIndex = $index
            break
        }
    }
    $form.Controls.Add($combo)

    $continueButton = New-Object System.Windows.Forms.Button
    $continueButton.Text = 'Continue / 继续'
    $continueButton.Size = New-Object System.Drawing.Size(132, 36)
    $continueButton.Location = New-Object System.Drawing.Point(335, 175)
    $continueButton.Add_Click({
        $form.Tag = [string]$combo.SelectedItem.Code
        $form.DialogResult = [System.Windows.Forms.DialogResult]::OK
        $form.Close()
    })
    $form.AcceptButton = $continueButton
    $form.Controls.Add($continueButton)

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = 'Cancel'
    $cancelButton.Size = New-Object System.Drawing.Size(100, 36)
    $cancelButton.Location = New-Object System.Drawing.Point(225, 175)
    $cancelButton.Add_Click({
        $form.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
        $form.Close()
    })
    $form.CancelButton = $cancelButton
    $form.Controls.Add($cancelButton)

    $result = $form.ShowDialog()
    if ($result -ne [System.Windows.Forms.DialogResult]::OK) { return $null }
    return [string]$form.Tag
}

function Test-SystemDesktopRuntime {
    try {
        $dotnet = Get-Command 'dotnet.exe' -ErrorAction Stop
        $runtimes = & $dotnet.Source --list-runtimes 2>$null
        return @($runtimes | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' }).Count -gt 0
    }
    catch {
        return $false
    }
}

function Get-DefaultInstallPath {
    $localAppData = [Environment]::GetFolderPath('LocalApplicationData')
    return Join-Path $localAppData 'Programs\TavernDesk'
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

function Normalize-InstallPath {
    param([string]$RawPath, [string]$Culture)

    if ([string]::IsNullOrWhiteSpace($RawPath)) {
        throw (Get-Text $Culture 'InvalidPath')
    }

    try {
        $expanded = [Environment]::ExpandEnvironmentVariables($RawPath.Trim())
        $fullPath = [IO.Path]::GetFullPath($expanded).TrimEnd('\')
    }
    catch {
        throw (Get-Text $Culture 'InvalidPath')
    }

    if ([string]::IsNullOrWhiteSpace($fullPath)) {
        throw (Get-Text $Culture 'InvalidPath')
    }

    $blocked = @(
        [IO.Path]::GetPathRoot($fullPath),
        [Environment]::GetFolderPath('Windows'),
        [Environment]::GetFolderPath('UserProfile'),
        [Environment]::GetFolderPath('LocalApplicationData'),
        [Environment]::GetFolderPath('ApplicationData'),
        [Environment]::GetFolderPath('ProgramFiles'),
        [Environment]::GetFolderPath('ProgramFilesX86')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) }

    foreach ($blockedPath in $blocked) {
        if ([string]::Equals(
                $fullPath,
                ([IO.Path]::GetFullPath($blockedPath).TrimEnd('\')),
                [StringComparison]::OrdinalIgnoreCase)) {
            throw (Get-Text $Culture 'UnsafePath')
        }
    }

    return $fullPath
}

function Test-ManagedInstall {
    param([string]$TargetPath)

    $markerPath = Join-Path $TargetPath $script:MarkerFileName
    if (-not (Test-Path -LiteralPath $markerPath -PathType Leaf)) { return $false }
    try {
        $marker = Get-Content -LiteralPath $markerPath -Raw -Encoding UTF8 | ConvertFrom-Json
        return $marker.product -eq $script:ProductName
    }
    catch {
        return $false
    }
}

function Test-DevelopmentWorkspace {
    param([string]$TargetPath)
    return (Test-Path -LiteralPath (Join-Path $TargetPath '.git')) `
        -or (Test-Path -LiteralPath (Join-Path $TargetPath 'src')) `
        -or (Test-Path -LiteralPath (Join-Path $TargetPath 'TavernDesk.sln'))
}

function Test-AppRunningFromTarget {
    param([string]$TargetPath)

    foreach ($process in @(Get-Process -Name 'TavernDesk.App' -ErrorAction SilentlyContinue)) {
        try {
            if ($process.Path -and $process.Path.StartsWith(
                    $TargetPath.TrimEnd('\') + '\',
                    [StringComparison]::OrdinalIgnoreCase)) {
                return $true
            }
        }
        catch { }
    }
    return $false
}

function Assert-ParentWritable {
    param([string]$ParentPath, [string]$Culture)

    try {
        [IO.Directory]::CreateDirectory($ParentPath) | Out-Null
        $probePath = Join-Path $ParentPath ('.taverndesk-write-' + [Guid]::NewGuid().ToString('N') + '.tmp')
        [IO.File]::WriteAllText($probePath, 'probe')
        [IO.File]::Delete($probePath)
    }
    catch {
        throw (Get-Text $Culture 'NotWritable')
    }
}

function Remove-SafeSiblingDirectory {
    param([string]$Path, [string]$ExpectedParent, [string]$ExpectedPrefix)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullParent = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\')
    $actualParent = [IO.Path]::GetDirectoryName($fullPath).TrimEnd('\')
    $leaf = [IO.Path]::GetFileName($fullPath)
    if (-not [string]::Equals($actualParent, $fullParent, [StringComparison]::OrdinalIgnoreCase) `
        -or -not $leaf.StartsWith($ExpectedPrefix, [StringComparison]::Ordinal)) {
        throw 'Installer refused to remove an unexpected directory.'
    }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

function Remove-ManagedInstallDirectory {
    param([string]$TargetPath)

    $fullPath = [IO.Path]::GetFullPath($TargetPath).TrimEnd('\')
    if (-not (Test-ManagedInstall $fullPath)) {
        throw 'Installer refused to remove an unmanaged directory.'
    }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

function New-Shortcut {
    param(
        [string]$ShortcutPath,
        [string]$TargetPath,
        [string]$WorkingDirectory,
        [string]$IconPath,
        [string]$Arguments = ''
    )

    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ShortcutPath)) | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = $IconPath + ',0'
    if (-not [string]::IsNullOrWhiteSpace($Arguments)) {
        $shortcut.Arguments = $Arguments
    }
    $shortcut.Save()
}

function Write-InstallMarker {
    param(
        [string]$TargetPath,
        [string]$Culture,
        [bool]$DesktopShortcut,
        [bool]$StartMenuShortcut
    )

    $appExe = Join-Path $TargetPath 'app\TavernDesk.App.exe'
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($appExe).ProductVersion
    $marker = [ordered]@{
        product = $script:ProductName
        version = $version
        installerLanguage = $Culture
        selfContained = $true
        registryEntriesCreated = $false
        desktopShortcut = $DesktopShortcut
        startMenuShortcut = $StartMenuShortcut
        installedAt = [DateTimeOffset]::Now.ToString('O')
    } | ConvertTo-Json
    [IO.File]::WriteAllText(
        (Join-Path $TargetPath $script:MarkerFileName),
        $marker,
        (New-Object Text.UTF8Encoding($true)))
}

function Install-TavernDeskPayload {
    param(
        [string]$TargetPath,
        [string]$Culture,
        [bool]$CreateDesktopShortcut,
        [bool]$CreateStartMenuShortcut
    )

    if (-not (Test-Path -LiteralPath $script:PayloadArchive -PathType Leaf)) {
        throw (Get-Text $Culture 'PayloadMissing')
    }
    if (Test-DevelopmentWorkspace $TargetPath) {
        throw (Get-Text $Culture 'DevelopmentDirectory')
    }
    if (Test-AppRunningFromTarget $TargetPath) {
        throw (Get-Text $Culture 'AppRunning')
    }

    $targetExists = Test-Path -LiteralPath $TargetPath -PathType Container
    if ($targetExists) {
        $entries = @(Get-ChildItem -LiteralPath $TargetPath -Force)
        if ($entries.Count -gt 0 -and -not (Test-ManagedInstall $TargetPath)) {
            throw (Get-Text $Culture 'UnknownDirectory')
        }
    }

    $parentPath = [IO.Path]::GetDirectoryName($TargetPath)
    Assert-ParentWritable $parentPath $Culture
    $suffix = [Guid]::NewGuid().ToString('N')
    $stagePath = Join-Path $parentPath ('.TavernDesk.installing.' + $suffix)
    $backupPath = Join-Path $parentPath ('.TavernDesk.backup.' + $suffix)
    $oldMoved = $false
    $newMoved = $false
    $desktopPath = Join-Path (Get-DesktopFolder) 'TavernDesk.lnk'
    $startMenuPath = Join-Path (Get-ProgramsFolder) 'TavernDesk'

    try {
        [IO.Directory]::CreateDirectory($stagePath) | Out-Null
        Expand-Archive -LiteralPath $script:PayloadArchive -DestinationPath $stagePath -Force

        $launcher = Join-Path $stagePath 'TavernDesk.exe'
        $appExe = Join-Path $stagePath 'app\TavernDesk.App.exe'
        $runtimeConfig = Join-Path $stagePath 'app\TavernDesk.App.runtimeconfig.json'
        if (-not (Test-Path -LiteralPath $launcher -PathType Leaf) `
            -or -not (Test-Path -LiteralPath $appExe -PathType Leaf) `
            -or -not (Test-Path -LiteralPath $runtimeConfig -PathType Leaf) `
            -or -not ((Get-Content -LiteralPath $runtimeConfig -Raw -Encoding UTF8) -match 'includedFrameworks')) {
            throw (Get-Text $Culture 'PayloadMissing')
        }

        if ($targetExists) {
            if (@(Get-ChildItem -LiteralPath $TargetPath -Force).Count -eq 0) {
                [IO.Directory]::Delete($TargetPath)
            }
            else {
                Move-Item -LiteralPath $TargetPath -Destination $backupPath
                $oldMoved = $true
            }
        }

        Move-Item -LiteralPath $stagePath -Destination $TargetPath
        $newMoved = $true

        $uninstallCommand = @'
@echo off
powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -File "%~dp0Uninstall-TavernDesk.ps1"
'@
        [IO.File]::WriteAllText(
            (Join-Path $TargetPath 'Uninstall TavernDesk.cmd'),
            $uninstallCommand.TrimStart() + "`r`n",
            [Text.Encoding]::ASCII)

        Write-InstallMarker $TargetPath $Culture $CreateDesktopShortcut $CreateStartMenuShortcut

        $installedLauncher = Join-Path $TargetPath 'TavernDesk.exe'
        if ($CreateDesktopShortcut) {
            New-Shortcut $desktopPath $installedLauncher $TargetPath $installedLauncher
        }
        elseif (Test-Path -LiteralPath $desktopPath) {
            Remove-Item -LiteralPath $desktopPath -Force
        }

        if ($CreateStartMenuShortcut) {
            New-Shortcut `
                (Join-Path $startMenuPath 'TavernDesk.lnk') `
                $installedLauncher `
                $TargetPath `
                $installedLauncher
            New-Shortcut `
                (Join-Path $startMenuPath 'Uninstall TavernDesk.lnk') `
                (Join-Path $TargetPath 'Uninstall TavernDesk.cmd') `
                $TargetPath `
                $installedLauncher
        }
        elseif (Test-Path -LiteralPath $startMenuPath) {
            Remove-Item -LiteralPath $startMenuPath -Recurse -Force
        }

        if ($oldMoved) {
            Remove-SafeSiblingDirectory $backupPath $parentPath '.TavernDesk.backup.'
            $oldMoved = $false
        }
    }
    catch {
        if (Test-Path -LiteralPath $desktopPath -PathType Leaf) {
            Remove-Item -LiteralPath $desktopPath -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $startMenuPath -PathType Container) {
            Remove-Item -LiteralPath $startMenuPath -Recurse -Force -ErrorAction SilentlyContinue
        }
        if ($newMoved -and (Test-ManagedInstall $TargetPath)) {
            Remove-ManagedInstallDirectory $TargetPath
        }
        if ($oldMoved -and (Test-Path -LiteralPath $backupPath -PathType Container)) {
            Move-Item -LiteralPath $backupPath -Destination $TargetPath
        }
        throw
    }
    finally {
        if (Test-Path -LiteralPath $stagePath -PathType Container) {
            Remove-SafeSiblingDirectory $stagePath $parentPath '.TavernDesk.installing.'
        }
    }
}

function Show-SetupForm {
    param([string]$Culture, [bool]$RuntimePresent, [string]$DefaultPath)

    $form = New-Object System.Windows.Forms.Form
    $form.Text = Get-Text $Culture 'SetupTitle'
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $form.ClientSize = New-Object System.Drawing.Size(690, 455)
    $form.Font = New-Object System.Drawing.Font('Segoe UI', 10)
    $form.AutoScaleMode = 'Dpi'

    $heading = New-Object System.Windows.Forms.Label
    $heading.Text = Get-Text $Culture 'Heading'
    $heading.Font = New-Object System.Drawing.Font('Segoe UI Semibold', 17)
    $heading.AutoSize = $true
    $heading.Location = New-Object System.Drawing.Point(30, 24)
    $form.Controls.Add($heading)

    $intro = New-Object System.Windows.Forms.Label
    $intro.Text = Get-Text $Culture 'Intro'
    $intro.Location = New-Object System.Drawing.Point(33, 67)
    $intro.Size = New-Object System.Drawing.Size(625, 48)
    $form.Controls.Add($intro)

    $runtime = New-Object System.Windows.Forms.Label
    $runtime.Text = if ($RuntimePresent) { Get-Text $Culture 'RuntimePresent' } else { Get-Text $Culture 'RuntimeMissing' }
    $runtime.Location = New-Object System.Drawing.Point(33, 120)
    $runtime.Size = New-Object System.Drawing.Size(625, 48)
    $runtime.ForeColor = [System.Drawing.Color]::FromArgb(31, 89, 170)
    $form.Controls.Add($runtime)

    $registry = New-Object System.Windows.Forms.Label
    $registry.Text = Get-Text $Culture 'RegistryNote'
    $registry.Location = New-Object System.Drawing.Point(33, 168)
    $registry.Size = New-Object System.Drawing.Size(625, 45)
    $form.Controls.Add($registry)

    $pathLabel = New-Object System.Windows.Forms.Label
    $pathLabel.Text = Get-Text $Culture 'InstallLocation'
    $pathLabel.AutoSize = $true
    $pathLabel.Location = New-Object System.Drawing.Point(33, 224)
    $form.Controls.Add($pathLabel)

    $pathBox = New-Object System.Windows.Forms.TextBox
    $pathBox.Text = $DefaultPath
    $pathBox.Location = New-Object System.Drawing.Point(36, 251)
    $pathBox.Size = New-Object System.Drawing.Size(510, 30)
    $form.Controls.Add($pathBox)

    $browseButton = New-Object System.Windows.Forms.Button
    $browseButton.Text = Get-Text $Culture 'Browse'
    $browseButton.Location = New-Object System.Drawing.Point(558, 249)
    $browseButton.Size = New-Object System.Drawing.Size(100, 34)
    $browseButton.Add_Click({
        $dialog = New-Object System.Windows.Forms.FolderBrowserDialog
        $dialog.Description = Get-Text $Culture 'BrowseDescription'
        if (Test-Path -LiteralPath $pathBox.Text -PathType Container) {
            $dialog.SelectedPath = $pathBox.Text
        }
        if ($dialog.ShowDialog($form) -eq [System.Windows.Forms.DialogResult]::OK) {
            $pathBox.Text = Join-Path $dialog.SelectedPath 'TavernDesk'
        }
    })
    $form.Controls.Add($browseButton)

    $desktopCheck = New-Object System.Windows.Forms.CheckBox
    $desktopCheck.Text = Get-Text $Culture 'DesktopShortcut'
    $desktopCheck.Checked = $true
    $desktopCheck.AutoSize = $true
    $desktopCheck.Location = New-Object System.Drawing.Point(36, 300)
    $form.Controls.Add($desktopCheck)

    $startCheck = New-Object System.Windows.Forms.CheckBox
    $startCheck.Text = Get-Text $Culture 'StartMenuShortcut'
    $startCheck.Checked = $true
    $startCheck.AutoSize = $true
    $startCheck.Location = New-Object System.Drawing.Point(36, 332)
    $form.Controls.Add($startCheck)

    $launchCheck = New-Object System.Windows.Forms.CheckBox
    $launchCheck.Text = Get-Text $Culture 'LaunchAfter'
    $launchCheck.Checked = $true
    $launchCheck.AutoSize = $true
    $launchCheck.Location = New-Object System.Drawing.Point(36, 364)
    $form.Controls.Add($launchCheck)

    $cancelButton = New-Object System.Windows.Forms.Button
    $cancelButton.Text = Get-Text $Culture 'Cancel'
    $cancelButton.Location = New-Object System.Drawing.Point(438, 405)
    $cancelButton.Size = New-Object System.Drawing.Size(104, 36)
    $cancelButton.Add_Click({
        $form.DialogResult = [System.Windows.Forms.DialogResult]::Cancel
        $form.Close()
    })
    $form.CancelButton = $cancelButton
    $form.Controls.Add($cancelButton)

    $installButton = New-Object System.Windows.Forms.Button
    $installButton.Text = Get-Text $Culture 'Install'
    $installButton.Location = New-Object System.Drawing.Point(554, 405)
    $installButton.Size = New-Object System.Drawing.Size(104, 36)
    $installButton.Add_Click({
        try {
            $normalizedPath = Normalize-InstallPath $pathBox.Text $Culture
            $form.Tag = [pscustomobject]@{
                InstallPath = $normalizedPath
                DesktopShortcut = [bool]$desktopCheck.Checked
                StartMenuShortcut = [bool]$startCheck.Checked
                LaunchAfter = [bool]$launchCheck.Checked
            }
            $form.DialogResult = [System.Windows.Forms.DialogResult]::OK
            $form.Close()
        }
        catch {
            Show-ErrorMessage $Culture $_.Exception.Message
        }
    })
    $form.AcceptButton = $installButton
    $form.Controls.Add($installButton)

    if ($form.ShowDialog() -ne [System.Windows.Forms.DialogResult]::OK) { return $null }
    return $form.Tag
}

function Show-ProgressForm {
    param([string]$Culture)

    $form = New-Object System.Windows.Forms.Form
    $form.Text = Get-Text $Culture 'SetupTitle'
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.ControlBox = $false
    $form.ClientSize = New-Object System.Drawing.Size(520, 130)
    $form.Font = New-Object System.Drawing.Font('Segoe UI', 10)
    $form.AutoScaleMode = 'Dpi'

    $label = New-Object System.Windows.Forms.Label
    $label.Text = Get-Text $Culture 'Installing'
    $label.AutoSize = $true
    $label.Location = New-Object System.Drawing.Point(25, 23)
    $form.Controls.Add($label)

    $bar = New-Object System.Windows.Forms.ProgressBar
    $bar.Style = 'Marquee'
    $bar.MarqueeAnimationSpeed = 25
    $bar.Location = New-Object System.Drawing.Point(28, 63)
    $bar.Size = New-Object System.Drawing.Size(464, 28)
    $form.Controls.Add($bar)

    $form.Show()
    [System.Windows.Forms.Application]::DoEvents()
    return $form
}

$culture = Normalize-CultureName $Language
if (-not $Quiet -and [string]::IsNullOrWhiteSpace($Language)) {
    $culture = Show-LanguagePicker
    if ([string]::IsNullOrWhiteSpace($culture)) { exit 2 }
}
elseif ($Quiet -and -not [string]::IsNullOrWhiteSpace($env:TAVERNDESK_INSTALLER_TEST_LANGUAGE)) {
    $culture = Normalize-CultureName $env:TAVERNDESK_INSTALLER_TEST_LANGUAGE
}

try {
    if ($Quiet) {
        if ([string]::IsNullOrWhiteSpace($InstallPath)) {
            $InstallPath = $env:TAVERNDESK_INSTALLER_TEST_PATH
        }
        if ([string]::IsNullOrWhiteSpace($InstallPath)) {
            $InstallPath = Get-DefaultInstallPath
        }
        $choice = [pscustomobject]@{
            InstallPath = Normalize-InstallPath $InstallPath $culture
            DesktopShortcut = -not $NoDesktopShortcut
            StartMenuShortcut = -not $NoStartMenuShortcut
            LaunchAfter = $false
        }
    }
    else {
        $choice = Show-SetupForm $culture (Test-SystemDesktopRuntime) (Get-DefaultInstallPath)
        if ($null -eq $choice) { exit 2 }
    }

    $progress = $null
    if (-not $Quiet) { $progress = Show-ProgressForm $culture }
    try {
        Install-TavernDeskPayload `
            $choice.InstallPath `
            $culture `
            $choice.DesktopShortcut `
            $choice.StartMenuShortcut
    }
    finally {
        if ($null -ne $progress) { $progress.Close() }
    }

    if (-not $Quiet) {
        [System.Windows.Forms.MessageBox]::Show(
            (Get-Text $culture 'Success'),
            (Get-Text $culture 'SetupTitle'),
            [System.Windows.Forms.MessageBoxButtons]::OK,
            [System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
    }

    if ($choice.LaunchAfter -and -not $NoLaunch) {
        Start-Process `
            -FilePath (Join-Path $choice.InstallPath 'TavernDesk.exe') `
            -WorkingDirectory $choice.InstallPath
    }
    exit 0
}
catch {
    Show-ErrorMessage $culture ([string]::Format((Get-Text $culture 'Failure'), $_.Exception.Message))
    exit 1
}
