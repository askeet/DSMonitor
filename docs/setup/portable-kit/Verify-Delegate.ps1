# PowerShell 5.1+ | Windows-only | Offline, read-only. No model/API calls.
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$homePath = $env:USERPROFILE
if ([string]::IsNullOrWhiteSpace($homePath) -or -not (Test-Path -LiteralPath $homePath -PathType Container)) {
    throw 'A valid USERPROFILE is required.'
}

$script:failures = 0
$script:warnings = 0

function Show-Check {
    param([string]$Status, [string]$Message)
    $color = switch ($Status) {
        'OK'      { 'Green' }
        'MISSING' { 'Red' }
        default   { 'Yellow' }
    }
    Write-Host ('  {0,-7} {1}' -f $Status, $Message) -ForegroundColor $color
    if ($Status -eq 'MISSING') { $script:failures++ }
    if ($Status -eq 'WARN') { $script:warnings++ }
}

function Get-UserPath {
    param([string]$RelativePath)
    return (Join-Path $homePath ($RelativePath.Replace('/', '\')))
}

function Check-RequiredFile {
    param([string]$Label, [string]$RelativePath)
    $path = Get-UserPath $RelativePath
    if (Test-Path -LiteralPath $path -PathType Leaf) {
        Show-Check 'OK' $Label
    } else {
        Show-Check 'MISSING' "$Label ($RelativePath)"
    }
}

function Check-SkillLinks {
    param([string]$Label, [string]$RelativePath)
    $skillFile = Get-UserPath $RelativePath
    if (-not (Test-Path -LiteralPath $skillFile -PathType Leaf)) { return }

    try {
        $content = Get-Content -LiteralPath $skillFile -Raw -Encoding UTF8
        if ($content -notmatch '(?s)\A---\s*\r?\n.*?\r?\n---') {
            Show-Check 'WARN' "$Label: YAML frontmatter not detected"
        }
        $directory = Split-Path -Parent $skillFile
        $checked = 0
        foreach ($match in [regex]::Matches($content, '\[[^\]]+\]\(([^)]+)\)')) {
            $link = $match.Groups[1].Value.Trim()
            # Only check local file links, not HTTP URLs, anchors, or mail addresses.
            if ($link -match '^(?:#|[a-zA-Z][a-zA-Z0-9+.-]*:|[/\\])') { continue }
            $link = ($link -split '#', 2)[0]
            if ([string]::IsNullOrWhiteSpace($link)) { continue }
            $checked++
            try {
                $target = [IO.Path]::GetFullPath((Join-Path $directory ($link.Replace('/', '\'))))
                if (-not (Test-Path -LiteralPath $target -PathType Leaf)) {
                    Show-Check 'MISSING' "$Label: broken local link $link"
                }
            } catch {
                Show-Check 'WARN' "$Label: unable to verify link $link"
            }
        }
        Show-Check 'OK' "$Label: inspected $checked local Markdown link(s)"
    } catch {
        Show-Check 'WARN' "$Label: skill content could not be inspected"
    }
}

Write-Host 'Codex / OpenCode / Forge Delegate: OFFLINE configuration verification' -ForegroundColor Cyan
Write-Host 'No installers, authentication commands, agents, external API calls, or file changes.'
Write-Host ''

Write-Host 'Core configuration and global skills:'
Check-RequiredFile 'Global Codex instructions' '.codex/AGENTS.md'
Check-RequiredFile 'Codex configuration' '.codex/config.toml'
Check-RequiredFile 'Cost-efficient coding skill' '.agents/skills/cost-efficient-coding/SKILL.md'
Check-RequiredFile 'Delegate-work skill' '.agents/skills/delegate-work/SKILL.md'
Check-RequiredFile 'Forge Delegate configuration' '.forge-delegate/config.json'

Write-Host "`nSkill structure and local references:"
Check-SkillLinks 'cost-efficient-coding' '.agents/skills/cost-efficient-coding/SKILL.md'
Check-SkillLinks 'delegate-work' '.agents/skills/delegate-work/SKILL.md'

Write-Host "`nOpenCode configuration:"
$configs = @('.config/opencode/opencode.json', '.config/opencode/opencode.jsonc')
if (@($configs | Where-Object { Test-Path -LiteralPath (Get-UserPath $_) -PathType Leaf }).Count -gt 0) {
    Show-Check 'OK' 'OpenCode configuration found (JSON or JSONC)'
} else {
    Show-Check 'WARN' 'No OpenCode JSON/JSONC config found; installation may use defaults'
}
$agentDir = Get-UserPath '.config/opencode/agents'
if (Test-Path -LiteralPath $agentDir -PathType Container) {
    $agents = @(Get-ChildItem -LiteralPath $agentDir -File -Filter '*.md' -ErrorAction SilentlyContinue)
    if ($agents.Count -gt 0) {
        Show-Check 'OK' "OpenCode agent definitions found: $($agents.Count) (not executed)"
    } else {
        Show-Check 'WARN' 'OpenCode agents directory is empty'
    }
} else {
    Show-Check 'WARN' 'No custom OpenCode agents directory (built-in agents may still be used)'
}

Write-Host "`nMCP launcher candidates:"
$launchers = @(
    '.forge-delegate/start-mcp.cmd',
    '.forge-delegate/start-mcp.bat',
    '.forge-delegate/start-mcp.ps1',
    '.forge-delegate/launcher.mjs'
)
$foundLaunchers = @($launchers | Where-Object { Test-Path -LiteralPath (Get-UserPath $_) -PathType Leaf })
if ($foundLaunchers.Count -gt 0) {
    foreach ($item in $foundLaunchers) { Show-Check 'OK' "Launcher exists: $item" }
} else {
    Show-Check 'WARN' 'No conventional launcher found; direct MCP invocation may be valid'
}
Write-Host '  NOTE    Confirm the effective command/arguments in Codex config manually; launchers were not run.'

Write-Host "`nCommand availability (discovery only; commands not executed):"
foreach ($name in @('node', 'npm', 'opencode', 'forge-delegate')) {
    $command = Get-Command -Name $name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($null -ne $command) {
        Show-Check 'OK' "$name command found"
    } else {
        Show-Check 'WARN' "$name command not found in this shell's PATH"
    }
}

Write-Host "`nSummary: $script:failures required checks failed; $script:warnings warning(s)." -ForegroundColor Cyan
Write-Warning 'Offline file checks do not verify MCP connectivity, credentials, effective model/agent, provider access, step limits, or actual delegation.'
Write-Host 'Check MCP in Codex Desktop after restart. Run any paid smoke test only with separate approval.'
if ($script:failures -gt 0) { exit 1 }
exit 0
