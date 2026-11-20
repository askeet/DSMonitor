param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Setup', 'Check')]
    [string]$Mode
)

$ErrorActionPreference = 'Stop'

function Read-Markdown($Path) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    $stream = [IO.MemoryStream]::new($bytes)
    $reader = [IO.StreamReader]::new($stream, [Text.UTF8Encoding]::new($false, $true), $true)
    try {
        $text = $reader.ReadToEnd()
        @{ Bytes = $bytes; Text = $text; Encoding = $reader.CurrentEncoding }
    } finally {
        $reader.Dispose()
    }
}

function Get-Sections($Text) {
    $sections = @()
    $current = $null
    $fence = ''
    $comment = $false
    foreach ($line in ($Text -split '\r\n|\n|\r')) {
        $prose = $false
        if ($fence) {
            if ($line -match ('^ {0,3}' + [regex]::Escape($fence[0].ToString()) + '{' + $fence.Length + ',}[ \t]*$')) {
                $fence = ''
            }
        } elseif ($comment) {
            if ($line -match '-->') { $comment = $false }
        } elseif ($line -match '^ {0,3}<!--') {
            $comment = $line -notmatch '-->'
        } elseif ($line -match '^ {0,3}(`{3,}|~{3,})') {
            $fence = $Matches[1]
        } elseif ($line -match '^ {0,3}(#{1,2})(?:[ \t]+(.*)|$)') {
            $current = $null
            if ($Matches[1] -eq '##') {
                $title = ([string]$Matches[2] -replace '[ \t]+#+[ \t]*$', '').Trim()
                $current = @{ Title = $title; Lines = [Collections.Generic.List[string]]::new(); Prose = [Collections.Generic.List[string]]::new() }
                $sections += $current
                continue
            }
        } else {
            $prose = $true
        }
        if ($current) {
            $current.Lines.Add($line)
            if ($prose) { $current.Prose.Add($line) }
        }
    }
    @{ Sections = $sections; OpenBlock = [bool]$fence -or $comment }
}

function Save-Sections($Path, $Original, $Additions) {
    $newline = "`r`n"
    $encoding = [Text.UTF8Encoding]::new($false)
    $prefix = ''
    if ($Original) {
        $encoding = $Original.Encoding
        $lineEnding = [regex]::Match($Original.Text, '\r\n|\n|\r')
        if ($lineEnding.Success) { $newline = $lineEnding.Value }
        if ($Original.Text.Length -gt 0) {
            if ($Original.Text -notmatch '(\r\n|\n|\r)$') { $prefix += $newline }
            $prefix += $newline
        }
    }
    $suffix = $encoding.GetBytes($prefix + ($Additions -join ($newline * 2) -replace '\r\n|\n|\r', $newline) + $newline)
    $directory = [IO.Path]::GetDirectoryName($Path)
    $null = [IO.Directory]::CreateDirectory($directory)
    $temporary = Join-Path $directory ('.graphify-' + [guid]::NewGuid().ToString('N') + '.tmp')
    try {
        if ($Original) {
            if ([Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) -ne [Convert]::ToBase64String($Original.Bytes)) {
                throw "File changed during setup: $Path"
            }
            $backup = Join-Path ([IO.Path]::GetTempPath()) ('Graphify-' + [guid]::NewGuid().ToString('N') + '-' + [IO.Path]::GetFileName($Path))
            [IO.File]::WriteAllBytes($backup, $Original.Bytes)
            Write-Host "[INFO] Backup: $backup"
            [IO.File]::WriteAllBytes($temporary, [byte[]]($Original.Bytes + $suffix))
            [IO.File]::Replace($temporary, $Path, [NullString]::Value)
        } else {
            [IO.File]::WriteAllBytes($temporary, $suffix)
            [IO.File]::Move($temporary, $Path)
        }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

$failed = $false
foreach ($file in @(
    @{ Path = 'AGENTS.md'; Knowledge = 'Project Map' },
    @{ Path = '.github/copilot-instructions.md'; Knowledge = 'Project Knowledge' }
)) {
    try {
        $path = Join-Path (Get-Location).Path $file.Path
        $original = if (Test-Path -LiteralPath $path) { Read-Markdown $path }
        $target = Get-Sections $original.Text
        $additions = @()
        if ($Mode -eq 'Setup') {
            $template = Get-Sections (Read-Markdown (Join-Path $PSScriptRoot $file.Path)).Text
        }
        foreach ($title in @('Graphify', $file.Knowledge)) {
            $marker = if ($title -eq 'Graphify') { '.codex/skills/graphify/SKILL.md' } else { 'docs/AI/PROJECT_MAP.md' }
            $sections = @($target.Sections | Where-Object { $_.Title -eq $title })
            if ($sections.Count -gt 1) {
                Write-Host "[FAIL] $($file.Path) has duplicate '## $title' headings. Merge them manually."
                $failed = $true
            } elseif ($sections.Count -eq 1) {
                if (($sections[0].Prose -join "`n") -match [regex]::Escape($marker)) {
                    Write-Host "[PASS] $($file.Path) contains '## $title'."
                } else {
                    Write-Host "[FAIL] $($file.Path) section '## $title' is missing its reference to $marker. Review it manually."
                    $failed = $true
                }
            } elseif ($Mode -eq 'Check') {
                Write-Host "[FAIL] $($file.Path) is missing '## $title'."
                $failed = $true
            } elseif ($target.OpenBlock) {
                Write-Host "[FAIL] Close the code fence or HTML comment in $($file.Path) before appending '## $title'."
                $failed = $true
            } else {
                $source = @($template.Sections | Where-Object { $_.Title -eq $title })
                if ($source.Count -ne 1 -or $template.OpenBlock -or ($source[0].Prose -join "`n") -notmatch [regex]::Escape($marker)) {
                    throw "Invalid template section '## $title' in $($file.Path)"
                }
                if ((Read-Host "Append '## $title' to $($file.Path)? [y/N]") -match '^(y|yes)$') {
                    $additions += "## $title`n$(($source[0].Lines -join "`n").TrimEnd())"
                }
            }
        }
        if ($additions.Count) {
            Save-Sections $path $original $additions
            Write-Host "[PASS] Added $($additions.Count) section(s) to $($file.Path)."
        }
    } catch {
        Write-Host "[FAIL] $($file.Path): $_"
        $failed = $true
    }
}
if ($failed) { exit 2 }
exit 0
