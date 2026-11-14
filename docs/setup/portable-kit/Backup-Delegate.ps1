# PowerShell 5.1+ | Windows only | Back up configuration, not credential stores.
# ZIP is NOT encrypted. Inspect and encrypt it before moving or sharing.
[CmdletBinding()]
param(
    [string]$Destination = (Join-Path $PSScriptRoot 'backups')
)
$ErrorActionPreference = 'Stop'

$homePath = $env:USERPROFILE
if ([string]::IsNullOrWhiteSpace($homePath) -or -not (Test-Path -LiteralPath $homePath -PathType Container)) {
    throw 'USERPROFILE is unavailable.'
}

# Detect destinations inside a Git checkout without invoking Git or accessing its history.
function Test-InGitCheckout([string]$path) {
    $current = [System.IO.Path]::GetFullPath($path)
    while (-not (Test-Path -LiteralPath $current -PathType Container)) {
        $parent = Split-Path -Parent $current
        if (-not $parent -or $parent -eq $current) { return $false }
        $current = $parent
    }
    while ($current) {
        if (Test-Path -LiteralPath (Join-Path $current '.git')) { return $true }
        $parent = Split-Path -Parent $current
        if (-not $parent -or $parent -eq $current) { break }
        $current = $parent
    }
    return $false
}

Write-Host 'Codex / OpenCode / Forge Delegate PORTABLE BACKUP' -ForegroundColor Cyan
Write-Warning 'The ZIP is UNENCRYPTED; configuration files can contain embedded secrets.'
Write-Warning 'Credential stores, runtime databases, chat history and logs are not intentionally included.'
Write-Warning 'Project-level AGENTS.md permissions are NOT included. Back up repositories separately.'
Write-Host "Destination: $Destination"
if (Test-InGitCheckout $Destination) {
    Write-Warning 'Destination is inside a Git checkout. Choose an external private folder with -Destination to prevent accidental commits.'
    if ((Read-Host 'Type IN-REPO to acknowledge this risk, or anything else to cancel') -cne 'IN-REPO') {
        Write-Host 'Cancelled without changes.'; exit 0
    }
}
if ((Read-Host 'Type YES to create this unencrypted private backup') -cne 'YES') {
    Write-Host 'Cancelled without changes.'; exit 0
}

# Deliberate allowlist; never recursively copy the entire user configuration tree.
$files = @(
    '.codex/config.toml',
    '.codex/AGENTS.md',
    '.codex/requirements.toml',
    '.config/opencode/opencode.json',
    '.config/opencode/opencode.jsonc',
    '.config/opencode/tui.json',
    '.forge-delegate/config.json',
    '.forge-delegate/start-mcp.cmd',
    '.forge-delegate/start-mcp.bat',
    '.forge-delegate/start-mcp.ps1',
    '.forge-delegate/launcher.mjs'
)
$dirs = @(
    '.agents/skills/cost-efficient-coding', # Includes references/ recursively.
    '.agents/skills/delegate-work',
    '.config/opencode/agents',                # Do not hardcode the current worker filename.
    '.codex/agents',
    '.codex/rules'
)
$codexDir = Join-Path $homePath '.codex'
if (Test-Path -LiteralPath $codexDir -PathType Container) {
    Get-ChildItem -LiteralPath $codexDir -File -Filter '*.config.toml' | ForEach-Object {
        $files += '.codex/' + $_.Name
    }
}

# Directory-level filtering is necessary because a skill may carry its own assets.
# This cannot detect secrets embedded in otherwise allowed configuration files.
function Test-SensitiveFilename([string]$name) {
    return $name -match '^(?i:auth\.json|credentials?\.(?:json|toml|ya?ml)|secrets?\.(?:json|toml|ya?ml)|\.env(?:\..*)?|.*\.(?:db|sqlite|sqlite3|log|zip|7z|pfx|pem|key)|.*\.token)$'
}
function Test-ReparseInSourcePath([string]$fullPath) {
    # Check each existing component from the allowlisted file back to USERPROFILE.
    $root = [System.IO.Path]::GetFullPath($homePath).TrimEnd('\')
    $cursor = [System.IO.Path]::GetFullPath($fullPath)
    if (-not $cursor.StartsWith($root + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Source escapes USERPROFILE: $fullPath"
    }
    while ($cursor.Length -ge $root.Length) {
        $item = Get-Item -LiteralPath $cursor -Force -ErrorAction Stop
        if (($item.Attributes -band [System.IO.FileAttributes]::ReparsePoint) -ne 0) { return $true }
        if ($cursor.Equals($root, [System.StringComparison]::OrdinalIgnoreCase)) { break }
        $parent = Split-Path -Parent $cursor
        if (-not $parent -or $parent -eq $cursor) { break }
        $cursor = $parent
    }
    return $false
}
function Get-SafeDirectoryFiles([string]$directory) {