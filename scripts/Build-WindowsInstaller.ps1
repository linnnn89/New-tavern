param(
    [string]$OutputPath,
    [switch]$Force,
    [switch]$KeepBuildArtifacts
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')).TrimEnd('\')
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot 'TavernDesk-Setup-x64.exe'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)

if (Test-Path -LiteralPath $OutputPath) {
    if (-not $Force) {
        throw "Output already exists: $OutputPath"
    }
    [IO.File]::Delete($OutputPath)
}

$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$buildRoot = Join-Path $tempRoot ('TavernDeskInstallerBuild-' + [Guid]::NewGuid().ToString('N'))
$payloadRoot = Join-Path $buildRoot 'payload'
$publishRoot = Join-Path $payloadRoot 'app'
$packageSource = Join-Path $buildRoot 'package'
$archivePath = Join-Path $packageSource 'payload.zip'
$setupPath = Join-Path $packageSource 'setup.ps1'
$sedPath = Join-Path $buildRoot 'TavernDesk-Setup.sed'
$builtPackage = Join-Path $buildRoot 'TavernDesk-Setup-x64.exe'
$projectPath = Join-Path $repositoryRoot 'src\TavernDesk.App\TavernDesk.App.csproj'
$launcherPath = Join-Path $repositoryRoot 'TavernDesk.exe'
$licensePath = Join-Path $repositoryRoot 'LICENSE'
$setupSource = Join-Path $repositoryRoot 'installer\Install-TavernDesk.ps1'
$uninstallSource = Join-Path $repositoryRoot 'installer\Uninstall-TavernDesk.ps1'
$iexpressPath = Join-Path $env:WINDIR 'System32\iexpress.exe'
$neutralSourceRoot = '/_/TavernDesk'
$pathMap = $repositoryRoot + '=' + $neutralSourceRoot

function Remove-SafeBuildDirectory {
    param([string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Container)) { return }
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $parent = [IO.Path]::GetDirectoryName($fullPath).TrimEnd('\')
    $leaf = [IO.Path]::GetFileName($fullPath)
    if (-not [string]::Equals($parent, $tempRoot, [StringComparison]::OrdinalIgnoreCase) `
        -or -not $leaf.StartsWith('TavernDeskInstallerBuild-', [StringComparison]::Ordinal)) {
        throw 'Refusing to remove an unexpected build directory.'
    }
    Remove-Item -LiteralPath $fullPath -Recurse -Force
}

try {
    if (-not (Test-Path -LiteralPath $iexpressPath -PathType Leaf)) {
        throw 'Windows IExpress was not found.'
    }
    foreach ($requiredPath in @($projectPath, $launcherPath, $licensePath, $setupSource, $uninstallSource)) {
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Required installer input is missing: $requiredPath"
        }
    }

    [IO.Directory]::CreateDirectory($publishRoot) | Out-Null
    [IO.Directory]::CreateDirectory($packageSource) | Out-Null

    & dotnet restore $projectPath -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }
    & dotnet publish $projectPath `
        -c Release `
        --no-restore `
        -r win-x64 `
        --self-contained true `
        -p:PublishSingleFile=false `
        -p:DebugSymbols=false `
        -p:DebugType=None `
        -p:ContinuousIntegrationBuild=true `
        ("-p:PathMap=" + $pathMap) `
        -o $publishRoot
    if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

    Copy-Item -LiteralPath $launcherPath -Destination (Join-Path $payloadRoot 'TavernDesk.exe')
    Copy-Item -LiteralPath $licensePath -Destination (Join-Path $payloadRoot 'LICENSE.txt')

    $utf8Bom = New-Object Text.UTF8Encoding($true)
    $uninstallText = Get-Content -LiteralPath $uninstallSource -Raw -Encoding UTF8
    [IO.File]::WriteAllText(
        (Join-Path $payloadRoot 'Uninstall-TavernDesk.ps1'),
        $uninstallText,
        $utf8Bom)

    $runtimeConfigPath = Join-Path $publishRoot 'TavernDesk.App.runtimeconfig.json'
    if (-not (Test-Path -LiteralPath (Join-Path $publishRoot 'TavernDesk.App.exe') -PathType Leaf) `
        -or -not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf) `
        -or -not ((Get-Content -LiteralPath $runtimeConfigPath -Raw -Encoding UTF8) -match 'includedFrameworks')) {
        throw 'The published application is not a complete self-contained build.'
    }
    $requiredRuntimeFiles = @(
        'coreclr.dll',
        'hostfxr.dll',
        'PresentationFramework.dll',
        'Microsoft.ML.Tokenizers.dll',
        'Microsoft.ML.Tokenizers.Data.Cl100kBase.dll',
        'Microsoft.ML.Tokenizers.Data.O200kBase.dll',
        'e_sqlite3.dll'
    )
    $missingRuntimeFiles = @($requiredRuntimeFiles | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $publishRoot $_) -PathType Leaf)
    })
    if ($missingRuntimeFiles.Count -gt 0) {
        throw ('Required private runtime assets are missing: ' + ($missingRuntimeFiles -join ', '))
    }

    $forbiddenFiles = @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -Force -File | Where-Object {
        $_.Name -match '\.(pdb|db|db-wal|db-shm|log|user|suo|env)$' `
            -or $_.Name -ieq 'config.json' `
            -or $_.FullName -match '[\\/](tests|work|src|\.git|user-data)[\\/]'
    })
    if ($forbiddenFiles.Count -gt 0) {
        throw ('Forbidden development or personal files entered the payload: ' `
            + (($forbiddenFiles | ForEach-Object FullName) -join ', '))
    }

    $unexpectedRuntimeDirectories = @(Get-ChildItem -LiteralPath (Join-Path $publishRoot 'runtimes') -Directory -ErrorAction SilentlyContinue | Where-Object {
        $_.Name -ne 'win-x64'
    })
    if ($unexpectedRuntimeDirectories.Count -gt 0) {
        throw ('Unexpected non-win-x64 runtime assets were published: ' `
            + (($unexpectedRuntimeDirectories | ForEach-Object Name) -join ', '))
    }

    $privatePathPatterns = @(
        $repositoryRoot,
        [Environment]::GetFolderPath('UserProfile')
    ) | Where-Object { -not [string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique
    foreach ($payloadFile in @(Get-ChildItem -LiteralPath $payloadRoot -Recurse -File)) {
        $bytes = [IO.File]::ReadAllBytes($payloadFile.FullName)
        $asciiContent = [Text.Encoding]::ASCII.GetString($bytes)
        $unicodeContent = [Text.Encoding]::Unicode.GetString($bytes)
        foreach ($pattern in $privatePathPatterns) {
            if ($asciiContent.IndexOf($pattern, [StringComparison]::OrdinalIgnoreCase) -ge 0 `
                -or $unicodeContent.IndexOf($pattern, [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw "A local development path was found in the payload: $($payloadFile.FullName)"
            }
        }
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $payloadRoot,
        $archivePath,
        [IO.Compression.CompressionLevel]::Optimal,
        $false)

    $setupText = Get-Content -LiteralPath $setupSource -Raw -Encoding UTF8
    [IO.File]::WriteAllText($setupPath, $setupText, $utf8Bom)

    $sourceWithSlash = $packageSource.TrimEnd('\') + '\'
    $sed = @"
[Version]
Class=IEXPRESS
SEDVersion=3

[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=1
HideExtractAnimation=1
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles

[Strings]
InstallPrompt=
DisplayLicense=
FinishMessage=
TargetName=$builtPackage
FriendlyName=TavernDesk Setup
AppLaunched=powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File setup.ps1
PostInstallCmd=<None>
AdminQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File setup.ps1 -Quiet
UserQuietInstCmd=powershell.exe -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -STA -File setup.ps1 -Quiet
FILE0="setup.ps1"
FILE1="payload.zip"

[SourceFiles]
SourceFiles0=$sourceWithSlash

[SourceFiles0]
%FILE0%=
%FILE1%=
"@
    [IO.File]::WriteAllText($sedPath, $sed, [Text.Encoding]::ASCII)

    Push-Location $buildRoot
    try {
        $iexpressProcess = Start-Process `
            -FilePath $iexpressPath `
            -ArgumentList @('/N', '/Q', $sedPath) `
            -WindowStyle Hidden `
            -Wait `
            -PassThru
    }
    finally {
        Pop-Location
    }

    if (-not (Test-Path -LiteralPath $builtPackage -PathType Leaf)) {
        throw "IExpress did not create the installer executable (exit code $($iexpressProcess.ExitCode))."
    }
    Copy-Item -LiteralPath $builtPackage -Destination $OutputPath

    $hash = (Get-FileHash -LiteralPath $OutputPath -Algorithm SHA256).Hash
    $size = (Get-Item -LiteralPath $OutputPath).Length
    Write-Output "Installer: $OutputPath"
    Write-Output "Bytes: $size"
    Write-Output "SHA256: $hash"
}
finally {
    if ($KeepBuildArtifacts) {
        Write-Output "Build artifacts: $buildRoot"
    }
    else {
        Remove-SafeBuildDirectory $buildRoot
    }
}
