# PowerShell 5.1+ | Windows only | Restore a trusted Backup-Delegate ZIP.
# Existing files are skipped by default. No software installation or authentication changes.
[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$BackupZip,
    [switch]$Replace
)
$ErrorActionPreference = 'Stop'

$userProfilePath = $env:USERPROFILE
if ([string]::IsNullOrWhiteSpace($userProfilePath) -or -not (Test-Path -LiteralPath $userProfilePath -PathType Container)) {
    throw 'USERPROFILE is unavailable.'
}
if (-not $BackupZip) {
    $backupDir = Join-Path $PSScriptRoot 'backups'
    $candidate = Get-ChildItem -LiteralPath $backupDir -Filter 'DelegateBackup-*.zip' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if (-not $candidate) {
        throw "No backup found in $backupDir. Supply a ZIP path or drag it onto Restore-Delegate.bat."
    }
    $BackupZip = $candidate.FullName
}
$BackupZip = (Resolve-Path -LiteralPath $BackupZip -ErrorAction Stop).Path
if (-not (Test-Path -LiteralPath $BackupZip -PathType Leaf)) { throw 'Backup ZIP does not exist.' }

Write-Host "Codex / OpenCode / Forge Delegate RESTORE: $BackupZip" -ForegroundColor Cyan
Write-Warning 'Use only your own trusted, decrypted backup. SHA-256 checks detect damage, not malicious changes to the ZIP and its manifest.'
Write-Warning 'Existing files will be SKIPPED unless -Replace is specified.'
Write-Warning 'Restored configuration may contain secrets and executable launchers. Inspect before use.'
if ((Read-Host 'Type RESTORE to inspect and restore this backup') -cne 'RESTORE') {
    Write-Host 'Cancelled without changes.'; exit 0
}
if ($Replace -and (Read-Host 'Type REPLACE to permit replacing existing files') -cne 'REPLACE') {
    Write-Host 'Replacement cancelled without changes.'; exit 0
}

# Match Backup-Delegate.ps1's curated allowlist; do not restore arbitrary files into USERPROFILE.
$allowedFiles = @(
    '.codex/config.toml', '.codex/AGENTS.md', '.codex/requirements.toml',
    '.config/opencode/opencode.json', '.config/opencode/opencode.jsonc',
    '.config/opencode/tui.json',
    '.forge-delegate/config.json', '.forge-delegate/start-mcp.cmd',
    '.forge-delegate/start-mcp.bat', '.forge-delegate/start-mcp.ps1',
    '.forge-delegate/launcher.mjs'
)
$allowedPrefixes = @(
    '.agents/skills/cost-efficient-coding/',
    '.agents/skills/delegate-work/',
    '.config/opencode/agents/', '.codex/agents/', '.codex/rules/'
)
function Assert-RelativePath([string]$rel) {
    if ([string]::IsNullOrWhiteSpace($rel) -or $rel.Contains('\') -or $rel.Contains(':') -or
        $rel.StartsWith('/') -or $rel.EndsWith('/') -or $rel.Contains('//')) {
        throw "Invalid archive path: $rel"
    }
    foreach ($part in ($rel -split '/')) {
        if ($part -in @('.', '..') -or $part -match '[<>:"|?*\x00-\x1f]' -or
            $part -match '[. ]$' -or $part -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw "Unsafe Windows path component in: $rel"
        }
    }
}
function Assert-AllowlistedPath([string]$rel) {
    Assert-RelativePath $rel
    $name = ($rel -split '/')[-1]
    if ($name -match '^(?i:auth\.json|credentials?\.(?:json|toml|ya?ml)|secrets?\.(?:json|toml|ya?ml)|\.env(?:\..*)?|.*\.(?:db|sqlite|sqlite3|log|zip|7z|pfx|pem|key)|.*\.token)$') {
        throw "Sensitive file is not allowed in the restore: $rel"
    }
    if ($rel -in $allowedFiles -or $rel -match '(?i)^\.codex/[^/]+\.config\.toml$') { return }
    foreach ($prefix in $allowedPrefixes) {
        if ($rel.StartsWith($prefix, [System.StringComparison]::OrdinalIgnoreCase)) { return }
    }
    throw "Path is outside the portable backup allowlist: $rel"
}
function Assert-UnlinkedTarget([string]$target) {
    # A junction/symlink in a target parent could redirect writes outside USERPROFILE.
    $base = [System.IO.Path]::GetFullPath($userProfilePath).TrimEnd('\')
    $full = [System.IO.Path]::GetFullPath($target)
    if (-not $full.StartsWith($base + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Restore target escapes USERPROFILE: $target"
    }
    $cursor = $base
    $parts = $full.Substring($base.Length + 1) -split '\\'
    foreach ($part in $parts) {
        $cursor = Join-Path $cursor $part
        if (Test-Path -LiteralPath $cursor) {
            $item = Get-Item -LiteralPath $cursor -Force
            if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw "Restore target traverses a symlink or junction: $cursor"
            }
        }
    }
}
function Rebind-ProfileInText([string]$file, [string]$oldProfile, [string]$newProfile) {
    $extension = [System.IO.Path]::GetExtension($file).ToLowerInvariant()