param(
    [string]$ProjectRoot = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = 'Stop'
$localizationRoot = Join-Path $ProjectRoot 'src/TavernDesk.App/Localization'
$appRoot = Join-Path $ProjectRoot 'src/TavernDesk.App'
$cultures = @('zh-CN', 'zh-TW', 'en-US', 'ja-JP')
$xamlNamespace = 'http://schemas.microsoft.com/winfx/2006/xaml'
$placeholderPattern = '\{(\d+)(?:[^}]*)\}'
$catalogs = @{}

foreach ($culture in $cultures) {
    $path = Join-Path $localizationRoot "Strings.$culture.xaml"
    [xml]$document = Get-Content -LiteralPath $path -Raw -Encoding UTF8
    $entries = @{}
    foreach ($node in $document.ResourceDictionary.String) {
        $key = $node.GetAttribute('Key', $xamlNamespace)
        if ([string]::IsNullOrWhiteSpace($key)) {
            throw "$path contains a string without x:Key."
        }
        if ($entries.ContainsKey($key)) {
            throw "$path contains duplicate key '$key'."
        }
        $entries[$key] = $node.InnerText
    }

    $unexpectedBlankValues = @(
        $entries.Keys |
            Where-Object {
                [string]::IsNullOrWhiteSpace($entries[$_])
            } |
            Sort-Object
    )
    if ($unexpectedBlankValues.Count -gt 0) {
        throw "$culture contains unexpected blank values: $($unexpectedBlankValues -join ', ')."
    }

    $translationMarkers = @(
        $entries.GetEnumerator() |
            Where-Object { $_.Value -match '⟦|TODO|TBD|待翻' } |
            ForEach-Object { $_.Key } |
            Sort-Object
    )
    if ($translationMarkers.Count -gt 0) {
        throw "$culture contains unfinished translation markers: $($translationMarkers -join ', ')."
    }

    $catalogs[$culture] = $entries
}

$baseline = $catalogs['zh-CN']
foreach ($culture in $cultures | Where-Object { $_ -ne 'zh-CN' }) {
    $candidate = $catalogs[$culture]
    $missing = @($baseline.Keys | Where-Object { -not $candidate.ContainsKey($_) } | Sort-Object)
    $extra = @($candidate.Keys | Where-Object { -not $baseline.ContainsKey($_) } | Sort-Object)
    if ($missing.Count -gt 0 -or $extra.Count -gt 0) {
        throw "$culture key mismatch. Missing: $($missing -join ', '); Extra: $($extra -join ', ')."
    }

    foreach ($key in $baseline.Keys) {
        $expected = @([regex]::Matches($baseline[$key], $placeholderPattern) |
                ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        $actual = @([regex]::Matches($candidate[$key], $placeholderPattern) |
                ForEach-Object { $_.Groups[1].Value } | Sort-Object -Unique)
        if (($expected -join ',') -ne ($actual -join ',')) {
            throw "$culture placeholder mismatch at '$key': expected [$($expected -join ',')], actual [$($actual -join ',')]."
        }
    }
}

$protocolLiteralKeys = @(
    'Characters.DepthField',
    'Characters.RoleField',
    'Characters.Role.System',
    'Characters.Role.User',
    'Characters.Role.Assistant'
)
$unexpectedEnglishCopies = @(
    $baseline.Keys |
        Where-Object {
            $catalogs['en-US'][$_] -ceq $baseline[$_] -and
            $_ -notin $protocolLiteralKeys
        } |
        Sort-Object
)
if ($unexpectedEnglishCopies.Count -gt 0) {
    throw "en-US contains untranslated zh-CN values: $($unexpectedEnglishCopies -join ', ')."
}

$allowedMultilingualKeys = @(
    'FirstRun.Language.Title',
    'FirstRun.Language.Description',
    'Persona.MigratedUnsafe.MessageFormat'
)
$unexpectedEnglishHan = @(
    $catalogs['en-US'].GetEnumerator() |
        Where-Object {
            $_.Value -match '[\u3400-\u9FFF]' -and
            $_.Key -notin $allowedMultilingualKeys
        } |
        ForEach-Object { $_.Key } |
        Sort-Object
)
if ($unexpectedEnglishHan.Count -gt 0) {
    throw "en-US contains unexpected Han text: $($unexpectedEnglishHan -join ', ')."
}

$simplifiedJapanesePattern =
    '[这们为发后里载户话处导设录应请选过实认续现权辑资备级关复开删创默义书词经线总据]'
$unexpectedJapaneseSimplified = @(
    $catalogs['ja-JP'].GetEnumerator() |
        Where-Object {
            $_.Value -match $simplifiedJapanesePattern -and
            $_.Key -notin $allowedMultilingualKeys
        } |
        ForEach-Object { $_.Key } |
        Sort-Object
)
if ($unexpectedJapaneseSimplified.Count -gt 0) {
    throw "ja-JP contains unexpected Simplified Chinese characters: $($unexpectedJapaneseSimplified -join ', ')."
}

$taiwanTerminologyPattern =
    '软件|默认|创建|运行|交互|全局|模块|这里|那里|范围|用于|登录|调用|进程|自定义|服务商|数组|校验|导航|召回|连接|阈值|缓冲|后台|盘符|批量|访问|优先级|设备|恢复默认|字符'
$unexpectedTaiwanTerms = @(
    $catalogs['zh-TW'].GetEnumerator() |
        Where-Object { $_.Value -match $taiwanTerminologyPattern } |
        ForEach-Object { $_.Key } |
        Sort-Object
)
if ($unexpectedTaiwanTerms.Count -gt 0) {
    throw "zh-TW contains non-Taiwan terminology: $($unexpectedTaiwanTerms -join ', ')."
}

$hardcodedXaml = Get-ChildItem -LiteralPath $appRoot -Recurse -Filter '*.xaml' |
    Where-Object {
        $_.FullName -notlike "$localizationRoot*" -and
        $_.FullName -notmatch '\\(bin|obj)\\'
    } |
    Select-String -Pattern '[\p{IsCJKUnifiedIdeographs}]' -Encoding UTF8
if ($hardcodedXaml) {
    throw "Display CJK remains outside language dictionaries:`n$($hardcodedXaml -join "`n")"
}

$allowedCSharp = @(
    'LanguageRuntime.cs',
    'TimedPressFeedback.cs'
)
$hardcodedCSharp = Get-ChildItem -LiteralPath $appRoot -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    Select-String -Pattern '[\p{IsCJKUnifiedIdeographs}]' -Encoding UTF8 |
    Where-Object {
        $name = $_.Path | Split-Path -Leaf
        if ($allowedCSharp -contains $name) { return $false }
        if ($name -in @('PlayerPersonaManagerViewModel.cs', 'MemoryWorkflowViewModel.cs') -and
            $_.Line -match '"用户"') { return $false }
        if ($name -eq 'ChatViewModel.cs' -and $_.Line -match
            '当前用户并未发送回复|: "角色";|本轮只以|需要用户时|需要角色接力时|附加要求：') {
            return $false
        }
        return $true
    }
if ($hardcodedCSharp) {
    throw "Unclassified display CJK remains in App C#:`n$($hardcodedCSharp -join "`n")"
}

$nativeMessageBoxCalls = Get-ChildItem -LiteralPath $appRoot -Recurse -Filter '*.cs' |
    Where-Object {
        $_.Name -ne 'LocalizedMessageBox.cs' -and
        $_.FullName -notmatch '\\(bin|obj)\\'
    } |
    Select-String -Pattern '\bMessageBox\.Show\s*\(' -Encoding UTF8
if ($nativeMessageBoxCalls) {
    throw "Native MessageBox calls bypass application-language buttons:`n$($nativeMessageBoxCalls -join "`n")"
}

$unlocalizedButtonText = [System.Collections.Generic.List[string]]::new()
$buttonXamlFiles = Get-ChildItem -LiteralPath $appRoot -Recurse -Filter '*.xaml' |
    Where-Object {
        $_.FullName -notlike "$localizationRoot*" -and
        $_.FullName -notmatch '\\(bin|obj)\\'
    }
foreach ($file in $buttonXamlFiles) {
    [xml]$xaml = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    foreach ($button in @($xaml.SelectNodes('//*[local-name()="Button"]'))) {
        $values = [System.Collections.Generic.List[string]]::new()
        $content = $button.GetAttribute('Content')
        if (-not [string]::IsNullOrWhiteSpace($content)) {
            $values.Add($content)
        }
        foreach ($descendant in @($button.SelectNodes('.//*[@Text]'))) {
            $text = $descendant.GetAttribute('Text')
            if (-not [string]::IsNullOrWhiteSpace($text)) {
                $values.Add($text)
            }
        }
        foreach ($value in $values) {
            $isMarkup = $value.StartsWith('{')
            $isGlyphOnly = $value -match '^[\p{P}\p{S}\p{Co}\s]+$'
            if (-not $isMarkup -and -not $isGlyphOnly) {
                $relativePath = $file.FullName.Substring($appRoot.Length + 1)
                $unlocalizedButtonText.Add("${relativePath}: $value")
            }
        }
    }
}
if ($unlocalizedButtonText.Count -gt 0) {
    throw "Button text bypasses localization resources or bindings:`n$($unlocalizedButtonText -join "`n")"
}

$referencedKeys = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
$codeReferencePattern = 'LanguageRuntime\.(?:GetString|Format)\(\s*"([^"]+)"'
Get-ChildItem -LiteralPath $appRoot -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object {
        $source = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
        foreach ($match in [regex]::Matches($source, $codeReferencePattern)) {
            [void]$referencedKeys.Add($match.Groups[1].Value)
        }
    }

$stringPrefixes = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
foreach ($key in $baseline.Keys) {
    [void]$stringPrefixes.Add(($key -split '\.', 2)[0])
}

$resourceLikeLiteralPattern =
    '"([A-Za-z][A-Za-z0-9_-]*(?:\.[A-Za-z0-9_-]+)+)"'
Get-ChildItem -LiteralPath $appRoot -Recurse -Filter '*.cs' |
    Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } |
    ForEach-Object {
        $source = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
        foreach ($match in [regex]::Matches($source, $resourceLikeLiteralPattern)) {
            $key = $match.Groups[1].Value
            $prefix = ($key -split '\.', 2)[0]
            if ($stringPrefixes.Contains($prefix)) {
                [void]$referencedKeys.Add($key)
            }
        }
    }

$xamlReferencePattern = '\{(?:Dynamic|Static)Resource\s+([A-Za-z0-9_.-]+)\}'
Get-ChildItem -LiteralPath $appRoot -Recurse -Filter '*.xaml' |
    Where-Object {
        $_.FullName -notlike "$localizationRoot*" -and
        $_.FullName -notmatch '\\(bin|obj)\\'
    } |
    ForEach-Object {
        $source = Get-Content -LiteralPath $_.FullName -Raw -Encoding UTF8
        foreach ($match in [regex]::Matches($source, $xamlReferencePattern)) {
            $key = $match.Groups[1].Value
            $prefix = ($key -split '\.', 2)[0]
            if ($stringPrefixes.Contains($prefix)) {
                [void]$referencedKeys.Add($key)
            }
        }
    }

$missingReferencedKeys = @(
    $referencedKeys |
        Where-Object { -not $baseline.ContainsKey($_) } |
        Sort-Object
)
if ($missingReferencedKeys.Count -gt 0) {
    throw "UI code references missing localization keys: $($missingReferencedKeys -join ', ')."
}

$declaredXamlResources = [System.Collections.Generic.HashSet[string]]::new(
    [System.StringComparer]::Ordinal)
$allAppXaml = @(
    Get-ChildItem -LiteralPath $appRoot -Recurse -Filter '*.xaml' |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
)
foreach ($file in $allAppXaml) {
    $source = Get-Content -LiteralPath $file.FullName -Raw -Encoding UTF8
    foreach ($match in [regex]::Matches($source, 'x:Key\s*=\s*"([^"]+)"')) {
        [void]$declaredXamlResources.Add($match.Groups[1].Value)
    }
}

$missingXamlResources = [System.Collections.Generic.List[string]]::new()
foreach ($file in $allAppXaml) {
    $lineNumber = 0
    foreach ($line in Get-Content -LiteralPath $file.FullName -Encoding UTF8) {
        $lineNumber++
        foreach ($match in [regex]::Matches($line, $xamlReferencePattern)) {
            $key = $match.Groups[1].Value
            if (-not $declaredXamlResources.Contains($key)) {
                $relativePath = $file.FullName.Substring($appRoot.Length + 1)
                $missingXamlResources.Add("${relativePath}:${lineNumber} ${key}")
            }
        }
    }
}
if ($missingXamlResources.Count -gt 0) {
    throw "XAML references undeclared resources:`n$($missingXamlResources -join "`n")"
}

$protocolLiterals = @{
    'Characters.DepthField' = 'depth'
    'Characters.RoleField' = 'role'
    'Characters.Role.System' = 'system'
    'Characters.Role.User' = 'user'
    'Characters.Role.Assistant' = 'assistant'
}
foreach ($culture in $cultures) {
    foreach ($entry in $protocolLiterals.GetEnumerator()) {
        if ($catalogs[$culture][$entry.Key] -cne $entry.Value) {
            throw "$culture changed protocol literal '$($entry.Key)': expected '$($entry.Value)', actual '$($catalogs[$culture][$entry.Key])'."
        }
    }
}

$temporaryJapaneseInput = Join-Path $PSScriptRoot '_ja.tsv'
if (Test-Path -LiteralPath $temporaryJapaneseInput) {
    throw "Temporary Japanese translation input was not cleaned up: $temporaryJapaneseInput"
}

Write-Host "Localization verification passed: $($baseline.Count) keys across $($cultures.Count) cultures; $($referencedKeys.Count) localized and $($declaredXamlResources.Count) declared XAML resources checked."
