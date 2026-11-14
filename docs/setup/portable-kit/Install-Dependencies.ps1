# PowerShell 5.1+ | Windows-only | Explicit opt-in npm installation.
# Installs the selected package versions; does not configure MCP or authenticate accounts.
[CmdletBinding()]
param(
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$OpenCodeVersion = '2.2.6',

    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string]$ForgeVersion = '1.2.4'
)

$ErrorActionPreference = 'Stop'

$node = Get-Command 'node.exe' -ErrorAction SilentlyContinue
$npm = Get-Command 'npm.cmd' -ErrorAction SilentlyContinue
if (-not $node -or -not $npm) {
    throw 'Node.js and npm are required. Install Node.js separately with explicit approval, reopen the terminal, and rerun.'
}

$packages = @(
    "@opencode/cli@$OpenCodeVersion",
    "forge-delegate@$ForgeVersion"
)

Write-Host 'Codex / OpenCode / Forge Delegate dependency installer' -ForegroundColor Cyan
Write-Host 'Packages to install globally (exact versions):'
$packages | ForEach-Object { Write-Host "  - $_" }
Write-Host "Node.js: $((& $node.Source --version 2>&1 | Out-String).Trim())"
Write-Host "npm: $((& $npm.Source --version 2>&1 | Out-String).Trim())"
Write-Host 'Codex Desktop and Node.js are not installed by this script.'
Write-Host 'This script does not configure MCP, install skills, or sign in to any provider.'
Write-Warning 'npm downloads and executes third-party package code, including package lifecycle scripts.'
Write-Warning 'Global installation may install, replace, or downgrade packages and their dependencies in the npm global prefix.'
Write-Warning 'The version defaults are historical documented versions, not verified latest or recommended versions.'
Write-Host 'Use -OpenCodeVersion and -ForgeVersion to select other exact versions after verifying compatibility.'

if ((Read-Host 'Type INSTALL to proceed with both global npm installations') -cne 'INSTALL') {
    Write-Host 'Cancelled. No npm install command was run.'
    exit 0
}

foreach ($package in $packages) {
    Write-Host "Installing $package ..." -ForegroundColor Cyan
    & $npm.Source install --global $package
    if ($LASTEXITCODE -ne 0) {
        throw "npm install failed for $package (exit code $LASTEXITCODE). Earlier packages may already have been installed. No rollback was attempted."
    }
}

Write-Host 'npm reported successful installation of both requested packages.' -ForegroundColor Green
Write-Host 'Next: restore configuration, reauthenticate interactively, and run offline verification.'
Write-Host 'Installation success does not prove MCP readiness, authentication, or delegated execution.'

