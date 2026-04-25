[CmdletBinding()]
param(
    [string]$RepoRoot = (Get-Location).Path
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-GitLines {
    param(
        [string[]]$Arguments,
        [switch]$AllowFailure
    )

    $output = & git @Arguments 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -ne 0 -and -not $AllowFailure) {
        $commandText = 'git ' + ($Arguments -join ' ')
        throw "Command failed: $commandText`n$output"
    }

    if ($null -eq $output) {
        return ,@()
    }

    return ,@($output |
        Where-Object { $_ -is [string] } |
        ForEach-Object { $_.TrimEnd() } |
        Where-Object { $_ -ne '' })
}

function Find-PatternMatches {
    param(
        [string]$FullPath,
        [string]$RelativePath,
        [string]$Pattern,
        [string]$FindingType
    )

    try {
        return @(Select-String -Path $FullPath -Pattern $Pattern -AllMatches |
            ForEach-Object {
                $finding = [PSCustomObject]@{
                    Type = $FindingType
                    Path = $RelativePath
                    LineNumber = $_.LineNumber
                    Line = $_.Line.Trim()
                }

                if (Should-IgnoreAuditScriptPatternDefinition $finding) {
                    $null
                }
                else {
                    $finding
                }
            } |
            Where-Object { $null -ne $_ })
    }
    catch {
        return @()
    }
}

function Should-IgnoreAuditScriptPatternDefinition {
    param(
        [psobject]$Finding
    )

    if ($null -eq $Finding -or $Finding.Path -ne 'scripts/Verify-GitHubPushSafety.ps1') {
        return $false
    }

    return $Finding.Line -match '^\$(generatedPathPattern|machinePathPattern|secretPatterns)\s*='
        -or $Finding.Line -match "^\s*'[^']+'\s*=\s*'.+'$"
}

function Should-IgnoreAllowedPersonalDataFinding {
    param(
        [psobject]$Finding
    )

    if ($null -eq $Finding) {
        return $false
    }

    $allowedHolder = ('Hen' + 'rik') + ' ' + ('Nie' + 'mann')
    $allowedLine = "Copyright (c) 2026 $allowedHolder. All rights reserved."

    return $Finding.Path -eq 'LICENSE' -and $Finding.Line -eq $allowedLine
}

function New-WordPattern {
    param(
        [string[]]$Parts
    )

    return '\b' + [regex]::Escape(($Parts -join '')) + '\b'
}

$generatedPathPattern = '(^|/)(bin|obj|artifacts|TestResults|\.vs|outputs|\.local|LI-export)/'
$machinePathPattern = '(?i)C:\\Users\\[^\\\r\n]+|AppData\\Roaming\\NuGet|\.nuget\\packages'
$personalDataPatterns = [ordered]@{
    'Scrub term 001' = New-WordPattern @('Hen', 'rik')
    'Scrub term 002' = New-WordPattern @('Nie', 'mann')
    'Scrub term 003' = New-WordPattern @('hen', 'nie', '42')
    'Scrub term 004' = New-WordPattern @('By', 'sted')
    'Scrub term 005' = New-WordPattern @('Bas', 'set')
    'Scrub term 006' = New-WordPattern @('Pro', 'ventum')
    'Scrub term 007' = New-WordPattern @('Cyber', 'Business')
    'Scrub term 008' = New-WordPattern @('Web', 'Stuff')
    'Scrub term 009' = New-WordPattern @('Oti', 'con')
    'Scrub term 010' = New-WordPattern @('Saxo ', 'Bank')
    'Scrub term 011' = New-WordPattern @('Novo ', 'Nordisk')
    'Scrub term 012' = New-WordPattern @('PA ', 'Consulting')
    'Scrub term 013' = (New-WordPattern @('Foss', '.dk')) + '|' + (New-WordPattern @('FO', 'SS'))
    'Scrub term 014' = '\b' + 'e-' + '[Bb]' + 'oks' + '\b'
    'Scrub term 015' = New-WordPattern @('Sch', 'ultz')
    'Scrub term 016' = New-WordPattern @('F', 'D', 'M')
    'Scrub term 017' = New-WordPattern @('Co', 'op')
    'Scrub term 018' = New-WordPattern @('Trust', 'works')
    'Scrub term 019' = New-WordPattern @('Con', 'tinia')
}
$secretPatterns = [ordered]@{
    'Private key' = '(?m)^-----BEGIN [A-Z ]*PRIVATE KEY-----'
    'GitHub token' = '\b(?:ghp|github_pat|gho|ghu|ghs|ghr)_[A-Za-z0-9_]+\b'
    'OpenAI-style key' = '\bsk-[A-Za-z0-9_-]{20,}\b'
    'Google API key' = '\bAIza[0-9A-Za-z\-_]{20,}\b'
    'Slack token' = '\bxox[baprs]-[A-Za-z0-9-]{10,}\b'
    'Credential assignment' = '(?i)(password|passwd|pwd|secret|api[-_ ]?key|clientsecret|connectionstring)\s*["'']?\s*[:=]\s*["''][^"'']+["'']'
    'Bearer token literal' = '(?i)authorization\s*[:=]\s*["'']?bearer\s+[A-Za-z0-9._-]{20,}'
}

$resolvedRepoRoot = Resolve-Path -LiteralPath $RepoRoot
Push-Location $resolvedRepoRoot

try {
    $isWorkTree = @(& git rev-parse --is-inside-work-tree 2>$null)
    if ($LASTEXITCODE -ne 0 -or $isWorkTree[-1] -ne 'true') {
        throw 'The target path is not inside a git working tree.'
    }

    $trackedFiles = Get-GitLines -Arguments @('ls-files')
    $stagedFiles = Get-GitLines -Arguments @('diff', '--cached', '--name-only', '--diff-filter=ACMR') -AllowFailure
    $candidateFiles = @($trackedFiles + $stagedFiles | Sort-Object -Unique)
    $contentScanFiles = @($candidateFiles | Where-Object { $_ -ne 'scripts/Verify-GitHubPushSafety.ps1' })

    Write-Host "Repo root: $resolvedRepoRoot"
    Write-Host "Tracked files: $($trackedFiles.Count)"
    Write-Host "Staged files: $($stagedFiles.Count)"
    Write-Host "Files scanned: $($contentScanFiles.Count)"

    if ($candidateFiles.Count -eq 0) {
        Write-Host 'No tracked or staged files were found to audit.'
        exit 0
    }

    $generatedPathFindings = @($candidateFiles | Where-Object { $_ -match $generatedPathPattern })
    $machinePathFindings = @()
    $personalDataFindings = @()
    $secretFindings = @()

    foreach ($relativePath in $contentScanFiles) {
        $fullPath = Join-Path $resolvedRepoRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            continue
        }

        $machinePathFindings += Find-PatternMatches -FullPath $fullPath -RelativePath $relativePath -Pattern $machinePathPattern -FindingType 'Machine-specific path'

        foreach ($pair in $personalDataPatterns.GetEnumerator()) {
            $personalDataFindings += Find-PatternMatches -FullPath $fullPath -RelativePath $relativePath -Pattern $pair.Value -FindingType $pair.Key
        }

        foreach ($pair in $secretPatterns.GetEnumerator()) {
            $secretFindings += Find-PatternMatches -FullPath $fullPath -RelativePath $relativePath -Pattern $pair.Value -FindingType $pair.Key
        }
    }

    $machinePathFindings = @($machinePathFindings | Where-Object { -not (Should-IgnoreAuditScriptPatternDefinition $_) })
    $personalDataFindings = @($personalDataFindings | Where-Object { -not (Should-IgnoreAllowedPersonalDataFinding $_) })
    $secretFindings = @($secretFindings | Where-Object { -not (Should-IgnoreAuditScriptPatternDefinition $_) })

    $hasFailures = $false

    if ($generatedPathFindings.Count -gt 0) {
        $hasFailures = $true
        Write-Host ''
        Write-Host 'Indexed generated-output paths detected:' -ForegroundColor Red
        $generatedPathFindings | Select-Object -First 20 | ForEach-Object { Write-Host "  $_" }
    }

    if ($machinePathFindings.Count -gt 0) {
        $hasFailures = $true
        Write-Host ''
        Write-Host 'Machine-specific path findings:' -ForegroundColor Red
        $machinePathFindings | Select-Object -First 20 | ForEach-Object {
            Write-Host ("  {0}:{1} [{2}] {3}" -f $_.Path, $_.LineNumber, $_.Type, $_.Line)
        }
    }

    if ($personalDataFindings.Count -gt 0) {
        $hasFailures = $true
        Write-Host ''
        Write-Host 'Personal-data scrub-list findings:' -ForegroundColor Red
        $personalDataFindings | Select-Object -First 40 | ForEach-Object {
            Write-Host ("  {0}:{1} [{2}] {3}" -f $_.Path, $_.LineNumber, $_.Type, $_.Line)
        }
    }

    if ($secretFindings.Count -gt 0) {
        $hasFailures = $true
        Write-Host ''
        Write-Host 'Secret-like findings:' -ForegroundColor Red
        $secretFindings | Select-Object -First 20 | ForEach-Object {
            Write-Host ("  {0}:{1} [{2}] {3}" -f $_.Path, $_.LineNumber, $_.Type, $_.Line)
        }
    }

    if ($hasFailures) {
        Write-Host ''
        Write-Host 'Push-safety audit failed.' -ForegroundColor Red
        exit 1
    }

    Write-Host ''
    Write-Host 'Push-safety audit passed.' -ForegroundColor Green
    exit 0
}
finally {
    Pop-Location
}