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
        return @(Select-String -Path $FullPath -Pattern $Pattern -AllMatches | ForEach-Object {
            [PSCustomObject]@{
                Type = $FindingType
                Path = $RelativePath
                LineNumber = $_.LineNumber
                Line = $_.Line.Trim()
            }
        })
    }
    catch {
        return @()
    }
}

$generatedPathPattern = '(^|/)(bin|obj|artifacts|TestResults|\.vs|outputs|\.local|LI-export)/'
$machinePathPattern = '(?i)C:\\Users\\[^\\\r\n]+|AppData\\Roaming\\NuGet|\.nuget\\packages'
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

    Write-Host "Repo root: $resolvedRepoRoot"
    Write-Host "Tracked files: $($trackedFiles.Count)"
    Write-Host "Staged files: $($stagedFiles.Count)"
    Write-Host "Files scanned: $($candidateFiles.Count)"

    if ($candidateFiles.Count -eq 0) {
        Write-Host 'No tracked or staged files were found to audit.'
        exit 0
    }

    $generatedPathFindings = @($candidateFiles | Where-Object { $_ -match $generatedPathPattern })
    $machinePathFindings = @()
    $secretFindings = @()

    foreach ($relativePath in $candidateFiles) {
        $fullPath = Join-Path $resolvedRepoRoot $relativePath
        if (-not (Test-Path -LiteralPath $fullPath -PathType Leaf)) {
            continue
        }

        $machinePathFindings += Find-PatternMatches -FullPath $fullPath -RelativePath $relativePath -Pattern $machinePathPattern -FindingType 'Machine-specific path'

        foreach ($pair in $secretPatterns.GetEnumerator()) {
            $secretFindings += Find-PatternMatches -FullPath $fullPath -RelativePath $relativePath -Pattern $pair.Value -FindingType $pair.Key
        }
    }

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