
@echo off
setlocal EnableExtensions DisableDelayedExpansion

rem ============================================================
rem Graphify + Codex CLI Setup / Check
rem Windows 10 / 11
rem ============================================================
rem
rem Keep this installer in a shared GraphifySetup folder.
rem Do NOT copy the installer into every repository.
rem
rem Expected setup package:
rem
rem GraphifySetup/
rem   Graphify_Codex_Windows.bat
rem   Graphify_Codex_Setup.md
rem   AGENTS.md
rem   .codex/
rem     hooks.json
rem     skills/
rem       graphify/
rem         SKILL.md
rem         .graphify_version
rem         references/
rem
rem Usage:
rem   Graphify_Codex_Windows.bat
rem   Graphify_Codex_Windows.bat --check "D:\YourProject"
rem   Graphify_Codex_Windows.bat --setup "D:\YourProject"
rem
rem --check: inspect the existing integration.
rem --setup: install/configure with appropriate approvals.
rem
rem Existing agent instructions, skills, and hooks are preserved.
rem The bundled hooks.json is copied ONLY if the target is missing.
rem No Git hooks, background watchers, or global PATH edits.
rem No source-code or test modifications.
rem ============================================================

set "MODE=%~1"
set "PROJECT=%~2"
set "KIT=%~dp0"

set "GRAPHIFY_BIN="
set "UV_BIN="
set "CLI_VERSION="
set "SKILL_VERSION="
set "BACKUP_DIR="

set "HAS_WARN=0"
set "HAS_FAIL=0"

rem Local-only operation; do not select a paid LLM backend.
set "GRAPHIFY_BACKEND=none"
set "GRAPHIFY_QUERY_LOG_DISABLE=1"

rem ============================================================
rem Select operation
rem ============================================================

if /I "%MODE%"=="--check" goto MODE_OK
if /I "%MODE%"=="--setup" goto MODE_OK
if not "%MODE%"=="" goto USAGE

echo.
echo ================================================
echo       Graphify for Codex CLI - Windows
echo ================================================
echo.
echo 1. CHECK existing Graphify configuration
echo 2. SETUP Graphify for a project
echo.

choice /C 12 /M "Choose operation"

if errorlevel 2 (
    set "MODE=--setup"
) else (
    set "MODE=--check"
)

goto MODE_OK

:USAGE
echo.
echo [FAIL] Invalid arguments.
echo.
echo Usage:
echo   %~nx0 --check "D:\YourProject"
echo   %~nx0 --setup "D:\YourProject"
echo.
pause
exit /b 2

rem ============================================================
rem Resolve target project
rem ============================================================

:MODE_OK

if not defined PROJECT (
    echo.
    set /p "PROJECT=Enter absolute project path: "
)

if not defined PROJECT (
    echo [FAIL] A project path is required.
    pause
    exit /b 2
)

if not exist "%PROJECT%\." (
    echo [FAIL] Project folder not found:
    echo "%PROJECT%"
    pause
    exit /b 2
)

pushd "%PROJECT%" 2>nul

if errorlevel 1 (
    echo [FAIL] Cannot enter the project directory.
    pause
    exit /b 2
)

echo.
echo ======================================================
echo Graphify for Codex CLI
echo ======================================================
echo Mode:    %MODE%
echo Project: "%CD%"
echo Setup:   "%KIT%"
echo ======================================================
echo.

rem ============================================================
rem Check Git worktree
rem ============================================================

where git.exe >nul 2>&1

if errorlevel 1 goto GIT_UNAVAILABLE

git rev-parse --is-inside-work-tree >nul 2>&1

if errorlevel 1 goto GIT_UNAVAILABLE

echo [INFO] Existing Git changes:
git status --short
goto GIT_READY

:GIT_UNAVAILABLE
echo [WARN] Git worktree unavailable.
echo        Freshness verification will be limited.
set "HAS_WARN=1"

:GIT_READY

rem ============================================================
rem Resolve Graphify executable
rem ============================================================

call :RESOLVE_GRAPHIFY

if defined GRAPHIFY_BIN goto HAVE_GRAPHIFY

if /I "%MODE%"=="--setup" goto NEED_GRAPHIFY

echo [FAIL] Graphify executable not found.
echo        Run SETUP to install or configure it.
set "HAS_FAIL=1"

goto CHECK_INTEGRATION

rem ============================================================
rem Install Graphify when required
rem ============================================================

:NEED_GRAPHIFY

echo.
echo [MISSING] Graphify CLI is not installed or discoverable.
echo.

choice /C YN /M "Approve installation of Graphify (graphifyy)?"

if errorlevel 2 goto INSTALL_DECLINED

call :RESOLVE_UV

if defined UV_BIN goto INSTALL_GRAPHIFY

echo.
echo [MISSING] uv executable not found.
echo.

where winget.exe >nul 2>&1

if errorlevel 1 (
    echo [FAIL] winget is unavailable.
    echo Install uv manually and rerun this script.
    set "HAS_FAIL=1"
    goto CHECK_INTEGRATION
)

choice /C YN /M "Approve installation of uv using winget?"

if errorlevel 2 goto INSTALL_DECLINED

winget install --id astral-sh.uv --exact

if errorlevel 1 goto INSTALL_FAILURE

call :RESOLVE_UV

if not defined UV_BIN (
    echo [WARN] uv installed but is not discoverable yet.
    echo Open a new terminal and rerun SETUP.
    set "HAS_FAIL=1"
    goto CHECK_INTEGRATION
)

:INSTALL_GRAPHIFY

echo.
echo [INFO] Installing graphifyy using uv.
echo.

"%UV_BIN%" tool install graphifyy

if errorlevel 1 goto INSTALL_FAILURE

call :RESOLVE_GRAPHIFY

if not defined GRAPHIFY_BIN (
    echo [FAIL] Graphify installed but executable was not found.
    echo Open a new terminal and rerun SETUP.
    set "HAS_FAIL=1"
    goto CHECK_INTEGRATION
)

goto HAVE_GRAPHIFY

:INSTALL_FAILURE

echo [FAIL] Package installation failed.
set "HAS_FAIL=1"
goto CHECK_INTEGRATION

:INSTALL_DECLINED

echo [INFO] Installation declined.
set "HAS_FAIL=1"
goto CHECK_INTEGRATION

rem ============================================================
rem Verify Graphify CLI
rem ============================================================

:HAVE_GRAPHIFY

echo.
echo [INFO] Graphify executable:
echo "%GRAPHIFY_BIN%"
echo.

"%GRAPHIFY_BIN%" --version

if errorlevel 1 (
    echo [FAIL] Graphify version check failed.
    set "HAS_FAIL=1"
    goto CHECK_INTEGRATION
)

rem Read CLI version for compatibility checks.
for /f "tokens=2" %%V in ('call "%GRAPHIFY_BIN%" --version 2^>nul') do if not defined CLI_VERSION set "CLI_VERSION=%%V"

if defined CLI_VERSION (
    echo [PASS] Graphify CLI version: %CLI_VERSION%
)

if /I "%MODE%"=="--setup" goto SETUP_PROJECT

goto CHECK_INTEGRATION

rem ============================================================
rem Project-local setup
rem ============================================================

:SETUP_PROJECT

call :SETUP_SKILL

if "%HAS_FAIL%"=="1" goto CHECK_INTEGRATION

call :SETUP_HOOK

if "%HAS_FAIL%"=="1" goto CHECK_INTEGRATION

call :SETUP_AGENTS

if "%HAS_FAIL%"=="1" goto CHECK_INTEGRATION

call :SETUP_IGNORE
if "%HAS_FAIL%"=="1" goto CHECK_INTEGRATION

call :SETUP_GRAPH

goto CHECK_INTEGRATION

rem ============================================================
rem Install or preserve Graphify skill
rem ============================================================


:SETUP_SKILL

set "SKILL_SRC=%KIT%.codex\skills\graphify"
set "SKILL_DST=.codex\skills\graphify"
set "SKILL_BACKUP="

rem Validate bundled skill before touching the target.
if not exist "%SKILL_SRC%\SKILL.md" goto SKILL_MISSING
if not exist "%SKILL_SRC%\.graphify_version" goto SKILL_MISSING
if not exist "%SKILL_SRC%\references\query.md" goto SKILL_MISSING
if not exist "%SKILL_SRC%\references\update.md" goto SKILL_MISSING

rem Existing skill: request approval and create backup.
if exist "%SKILL_DST%\" (
    echo [INFO] Existing Graphify skill found.
    choice /C YN /M "Update Graphify skill from setup package"
    if errorlevel 2 (
        echo [INFO] Existing Graphify skill preserved.
        call :CHECK_TARGET_SKILL_VERSION
        exit /b 0
    )

    set "SKILL_BACKUP=%TEMP%\GraphifySkillBackup_%RANDOM%_%RANDOM%"
    goto SKILL_BACKUP_EXISTING
)

goto SKILL_COPY

:SKILL_BACKUP_EXISTING

robocopy "%SKILL_DST%" "%SKILL_BACKUP%" /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto SKILL_BACKUP_FAILED
goto SKILL_COPY

:SKILL_COPY

rem /MIR removes obsolete references only inside graphify skill.
robocopy "%SKILL_SRC%" "%SKILL_DST%" /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
if errorlevel 8 goto SKILL_COPY_FAILED

if not exist "%SKILL_DST%\SKILL.md" goto SKILL_COPY_FAILED
if not exist "%SKILL_DST%\references\query.md" goto SKILL_COPY_FAILED
if not exist "%SKILL_DST%\references\update.md" goto SKILL_COPY_FAILED

echo [PASS] Graphify skill synchronized.
call :CHECK_TARGET_SKILL_VERSION
exit /b 0

:SKILL_MISSING
echo [FAIL] Bundled Graphify skill incomplete.
set "HAS_FAIL=1"
exit /b 1

:SKILL_BACKUP_FAILED
echo [FAIL] Could not back up existing Graphify skill.
set "HAS_FAIL=1"
exit /b 1

:SKILL_COPY_FAILED
echo [FAIL] Graphify skill synchronization failed.

if defined SKILL_BACKUP (
    echo [INFO] Restoring previous Graphify skill...
    robocopy "%SKILL_BACKUP%" "%SKILL_DST%" /MIR /R:1 /W:1 /NFL /NDL /NJH /NJS /NP >nul
    if errorlevel 8 (
        echo [FAIL] Restore failed. Backup: "%SKILL_BACKUP%"
    ) else (
        echo [PASS] Previous Graphify skill restored.
    )
)

set "HAS_FAIL=1"
exit /b 1


rem ============================================================
rem Version checks
rem ============================================================

:CHECK_KIT_SKILL_VERSION

set "SKILL_VERSION="

if not exist "%KIT%.codex\skills\graphify\.graphify_version" (
    echo [WARN] Bundled skill version file is missing.
    exit /b 1
)

for /f "usebackq delims=" %%V in ("%KIT%.codex\skills\graphify\.graphify_version") do if not defined SKILL_VERSION set "SKILL_VERSION=%%V"

if not defined SKILL_VERSION (
    echo [WARN] Bundled skill version is empty.
    exit /b 1
)

echo [INFO] Bundled skill version: %SKILL_VERSION%

if not defined CLI_VERSION exit /b 1

if /I not "%SKILL_VERSION%"=="%CLI_VERSION%" (
    echo [WARN] CLI and bundled skill versions differ.
    exit /b 1
)

echo [PASS] Bundled skill version matches CLI.

exit /b 0

:CHECK_TARGET_SKILL_VERSION

set "SKILL_VERSION="

if not exist ".codex\skills\graphify\.graphify_version" (
    echo [WARN] Existing skill has no version metadata.
    set "HAS_WARN=1"
    exit /b 0
)

for /f "usebackq delims=" %%V in (".codex\skills\graphify\.graphify_version") do if not defined SKILL_VERSION set "SKILL_VERSION=%%V"

if not defined SKILL_VERSION (
    echo [WARN] Existing skill version is empty.
    set "HAS_WARN=1"
    exit /b 0
)

echo [INFO] Existing skill version: %SKILL_VERSION%

if defined CLI_VERSION (
    if /I not "%SKILL_VERSION%"=="%CLI_VERSION%" (
        echo [WARN] Existing skill and CLI versions differ.
        echo        The existing skill was not overwritten.
        set "HAS_WARN=1"
    )
)

exit /b 0

rem ============================================================
rem Setup Codex hook
rem ============================================================

:SETUP_HOOK

rem Never overwrite existing hooks.json.
if exist ".codex\hooks.json" goto EXISTING_HOOK

rem Use the template beside this installer.
if not exist "%KIT%.codex\hooks.json" (
    echo [WARN] Bundled hooks.json was not found.
    set "HAS_WARN=1"
    exit /b 0
)

rem Validate JSON before copying.
powershell -NoProfile -NonInteractive -Command "try { $p=Join-Path $env:KIT '.codex\hooks.json'; $null=ConvertFrom-Json -InputObject ([IO.File]::ReadAllText($p)); exit 0 } catch { exit 1 }" >nul 2>&1

if errorlevel 1 (
    echo [FAIL] Bundled hooks.json contains invalid JSON.
    set "HAS_FAIL=1"
    exit /b 1
)

if not exist ".codex" (
    mkdir ".codex" >nul 2>&1
)

if errorlevel 1 (
    echo [FAIL] Cannot create .codex directory.
    set "HAS_FAIL=1"
    exit /b 1
)

copy "%KIT%.codex\hooks.json" ".codex\hooks.json" >nul

if errorlevel 1 (
    echo [FAIL] Could not copy hooks.json.
    set "HAS_FAIL=1"
    exit /b 1
)

echo [PASS] Graphify hooks.json copied from setup package.

rem The copied hook uses a PowerShell user-relative path.
rem Verify the referenced executable exists on this machine.
if not exist "%USERPROFILE%\.local\bin\graphify.exe" (
    echo [WARN] Hook template expects Graphify in .local\bin.
    echo        CLI may work through PATH, but hook execution is unverified.
    set "HAS_WARN=1"
)

exit /b 0

:EXISTING_HOOK

echo [PASS] Existing hooks.json preserved.

findstr /I /C:"hook-check" ".codex\hooks.json" >nul 2>&1

if errorlevel 1 (
    echo [WARN] Existing hooks.json has no Graphify hook-check.
    echo        No automatic merge was attempted.
    set "HAS_WARN=1"
) else (
    echo [INFO] Existing Graphify hook reference found.
)

exit /b 0

rem ============================================================
rem Setup AGENTS.md
rem ============================================================

:SETUP_AGENTS

if not exist "%KIT%AGENTS.md" (
    echo [FAIL] Setup package AGENTS.md template missing.
    set "HAS_FAIL=1"
    exit /b 1
)

rem Require the complete template, not only a heading.
findstr /I /C:"## Graphify" "%KIT%AGENTS.md" >nul 2>&1

if errorlevel 1 goto BAD_TEMPLATE

findstr /I /C:"graphify update" "%KIT%AGENTS.md" >nul 2>&1

if errorlevel 1 goto BAD_TEMPLATE

findstr /I /C:"graphify query" "%KIT%AGENTS.md" >nul 2>&1

if errorlevel 1 goto BAD_TEMPLATE

findstr /I /C:"Get-Command graphify" "%KIT%AGENTS.md" >nul 2>&1

if errorlevel 1 goto BAD_TEMPLATE

goto TEMPLATE_OK

:BAD_TEMPLATE

echo [FAIL] Bundled AGENTS.md contains incomplete Graphify rules.
echo        Expected queries, updates, and Windows CLI fallback.
set "HAS_FAIL=1"

exit /b 1

:TEMPLATE_OK

if exist "AGENTS.md" goto AGENTS_EXIST

echo.
choice /C YN /M "Create project AGENTS.md with Graphify instructions?"

if errorlevel 2 (
    echo [WARN] AGENTS.md creation declined.
    set "HAS_WARN=1"
    exit /b 0
)

copy "%KIT%AGENTS.md" "AGENTS.md" >nul

if errorlevel 1 (
    echo [FAIL] Could not create AGENTS.md.
    set "HAS_FAIL=1"
    exit /b 1
)

echo [PASS] AGENTS.md created.

exit /b 0

rem ============================================================
rem Existing AGENTS.md
rem ============================================================

:AGENTS_EXIST

findstr /I /C:"## Graphify" "AGENTS.md" >nul 2>&1

if not errorlevel 1 goto AGENTS_HAS_GRAPHIFY

echo.
echo [INFO] Existing AGENTS.md has no Graphify section.

choice /C YN /M "Append Graphify rules and preserve existing instructions?"

if errorlevel 2 (
    echo [WARN] Graphify instructions not appended.
    set "HAS_WARN=1"
    exit /b 0
)

call :BACKUP_CONFIG

if "%HAS_FAIL%"=="1" exit /b 1

rem Append as UTF-8 without replacing earlier instructions.
powershell -NoProfile -NonInteractive -Command "try { $p='AGENTS.md'; $src=Join-Path $env:KIT 'AGENTS.md'; $a=[IO.File]::ReadAllText($p,[Text.Encoding]::UTF8); $b=[IO.File]::ReadAllText($src,[Text.Encoding]::UTF8); $e=New-Object Text.UTF8Encoding($false); [IO.File]::WriteAllText($p,$a.TrimEnd([char]13,[char]10)+[Environment]::NewLine+[Environment]::NewLine+$b.TrimStart([char]0xFEFF),$e); exit 0 } catch { exit 1 }" >nul 2>&1

if errorlevel 1 (
    echo [FAIL] Could not append Graphify instructions.
    set "HAS_FAIL=1"
    exit /b 1
)

echo [PASS] Graphify rules appended.
echo [PASS] Existing project instructions preserved.

exit /b 0

rem ============================================================
rem Existing Graphify section
rem ============================================================

:AGENTS_HAS_GRAPHIFY

rem Detect a file containing only the empty Graphify heading.
powershell -NoProfile -NonInteractive -Command "$s=[IO.File]::ReadAllText('AGENTS.md'); if($s.Trim([char]0xFEFF).Trim() -eq '## Graphify'){exit 0}else{exit 1}" >nul 2>&1

if not errorlevel 1 goto AGENTS_ONLY_STUB

rem Check for important existing instructions.
findstr /I /C:"graphify update" "AGENTS.md" >nul 2>&1

if errorlevel 1 goto AGENTS_INCOMPLETE

findstr /I /C:"graphify query" "AGENTS.md" >nul 2>&1

if errorlevel 1 goto AGENTS_INCOMPLETE

findstr /I /C:"Get-Command graphify" "AGENTS.md" >nul 2>&1

if errorlevel 1 goto AGENTS_INCOMPLETE

findstr /I /C:"delegated" "AGENTS.md" >nul 2>&1

if errorlevel 1 goto AGENTS_INCOMPLETE

echo [PASS] Existing Graphify instructions preserved.

exit /b 0

:AGENTS_ONLY_STUB

echo.
echo [INFO] AGENTS.md contains only an empty Graphify heading.

choice /C YN /M "Replace the empty stub with complete Graphify rules?"

if errorlevel 2 (
    echo [WARN] Empty Graphify section preserved.
    set "HAS_WARN=1"
    exit /b 0
)

call :BACKUP_CONFIG

if "%HAS_FAIL%"=="1" exit /b 1

copy /Y "%KIT%AGENTS.md" "AGENTS.md" >nul

if errorlevel 1 (
    echo [FAIL] Could not replace AGENTS.md stub.
    set "HAS_FAIL=1"
    exit /b 1
)

echo [PASS] Empty Graphify section replaced.

exit /b 0

:AGENTS_INCOMPLETE

echo.
echo [WARN] Existing Graphify instructions appear incomplete.
echo        Existing AGENTS.md was NOT overwritten.
echo        Manually merge the Graphify section from the setup kit.
set "HAS_WARN=1"

exit /b 0


rem ============================================================
rem Setup .graphifyignore
rem ============================================================

:SETUP_IGNORE

rem Preserve existing project-specific exclusions.
if exist ".graphifyignore" (
    echo [PASS] Existing .graphifyignore preserved.
    exit /b 0
)

rem Check the shared setup template.
if not exist "%KIT%.graphifyignore" (
    echo [WARN] Setup package .graphifyignore not found.
    set "HAS_WARN=1"
    exit /b 0
)

rem Copy to the target repository root.
copy "%KIT%.graphifyignore" ".graphifyignore" >nul

if errorlevel 1 (
    echo [FAIL] Could not copy .graphifyignore.
    set "HAS_FAIL=1"
    exit /b 1
)

echo [PASS] .graphifyignore copied to project root.

exit /b 0


rem ============================================================
rem Build or update project graph
rem ============================================================

:SETUP_GRAPH

if exist "graphify-out\graph.json" goto GRAPH_UPDATE_OFFER

echo.
echo [INFO] No project knowledge graph found.
echo.
echo Review .graphifyignore before indexing.
echo Exclude project-specific bundled vendor directories.
echo Graphify extraction is local and code-only.
echo.

choice /C YN /M "Build initial code-only project graph?"

if errorlevel 2 (
    echo [WARN] Initial graph extraction skipped.
    set "HAS_WARN=1"
    exit /b 0
)

"%GRAPHIFY_BIN%" extract . --code-only

if errorlevel 1 (
    echo [FAIL] Graphify extraction failed.
    set "HAS_FAIL=1"
    exit /b 1
)

echo [PASS] Initial project graph generated.

exit /b 0

:GRAPH_UPDATE_OFFER

echo.
echo [INFO] Existing project graph detected.

choice /C YN /M "Run an incremental Graphify update?"

if errorlevel 2 (
    echo [WARN] Update skipped; graph freshness is unverified.
    set "HAS_WARN=1"
    exit /b 0
)

"%GRAPHIFY_BIN%" update .

if errorlevel 1 (
    echo [FAIL] Incremental Graphify update failed.
    set "HAS_FAIL=1"
    exit /b 1
)

echo [PASS] Graphify incremental update command succeeded.

exit /b 0

rem ============================================================
rem Backup original project configuration
rem ============================================================

:BACKUP_CONFIG

if defined BACKUP_DIR exit /b 0

set "BACKUP_DIR=%TEMP%\GraphifyCodexBackup_%RANDOM%_%RANDOM%"

mkdir "%BACKUP_DIR%" >nul 2>&1

if errorlevel 1 (
    echo [FAIL] Could not create backup directory.
    set "HAS_FAIL=1"
    exit /b 1
)

if exist "AGENTS.md" (
    copy /Y "AGENTS.md" "%BACKUP_DIR%\AGENTS.md" >nul
    if errorlevel 1 (
        echo [FAIL] Could not back up AGENTS.md.
        set "HAS_FAIL=1"
        exit /b 1
    )
)

if exist ".codex\hooks.json" (
    copy /Y ".codex\hooks.json" "%BACKUP_DIR%\hooks.json" >nul
    if errorlevel 1 (
        echo [FAIL] Could not back up hooks.json.
        set "HAS_FAIL=1"
        exit /b 1
    )
)

echo [INFO] Configuration backup:
echo "%BACKUP_DIR%"

exit /b 0

rem ============================================================
rem Check project integration
rem ============================================================

:CHECK_INTEGRATION

echo.
echo ======================================================
echo PROJECT INTEGRATION CHECKS
echo ======================================================
if exist ".graphifyignore" (
    echo [PASS] .graphifyignore exists.
) else (
    echo [WARN] .graphifyignore missing.
    set "HAS_WARN=1"
)
rem ------------------------------------------------------------
rem Codex CLI
rem ------------------------------------------------------------

where codex.cmd >nul 2>&1

if not errorlevel 1 goto CODEX_FOUND

where codex.exe >nul 2>&1

if not errorlevel 1 goto CODEX_FOUND

echo [WARN] Codex CLI launcher not found on PATH.
echo        It may still be available through Codex Desktop.
set "HAS_WARN=1"

goto CHECK_SKILL

:CODEX_FOUND

echo [PASS] Codex CLI launcher found.

rem ------------------------------------------------------------
rem Graphify skill
rem ------------------------------------------------------------

:CHECK_SKILL

if exist ".codex\skills\graphify\SKILL.md" (
    echo [PASS] Graphify SKILL.md found.
    call :CHECK_TARGET_SKILL_VERSION
) else (
    echo [FAIL] Graphify skill missing.
    set "HAS_FAIL=1"
)

if not exist ".codex\skills\graphify\references\" (
    echo [WARN] Graphify reference files missing.
    set "HAS_WARN=1"
)

rem ------------------------------------------------------------
rem AGENTS.md
rem ------------------------------------------------------------

if not exist "AGENTS.md" goto AGENTS_MISSING

findstr /I /C:"## Graphify" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INSUFFICIENT

findstr /I /C:"graphify query" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INSUFFICIENT

findstr /I /C:"graphify update" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INSUFFICIENT

findstr /I /C:"Get-Command graphify" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INSUFFICIENT

findstr /I /C:"delegated" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INSUFFICIENT

echo [PASS] AGENTS.md contains Graphify instructions.

goto CHECK_HOOK

:AGENTS_MISSING

echo [FAIL] AGENTS.md missing.
set "HAS_FAIL=1"

goto CHECK_HOOK

:AGENTS_INSUFFICIENT

echo [FAIL] Graphify instructions in AGENTS.md incomplete.
set "HAS_FAIL=1"

rem ------------------------------------------------------------
rem Codex hook
rem ------------------------------------------------------------

:CHECK_HOOK

if not exist ".codex\hooks.json" goto NO_HOOK

powershell -NoProfile -NonInteractive -Command "try { $null=ConvertFrom-Json -InputObject ([IO.File]::ReadAllText('.codex\hooks.json')); exit 0 } catch { exit 1 }" >nul 2>&1

if errorlevel 1 (
    echo [WARN] hooks.json contains invalid JSON.
    set "HAS_WARN=1"
    goto CHECK_GRAPH
)

echo [PASS] hooks.json is valid JSON.

findstr /I /C:"hook-check" ".codex\hooks.json" >nul 2>&1

if errorlevel 1 (
    echo [WARN] No Graphify hook-check reference found.
    set "HAS_WARN=1"
) else (
    echo [INFO] Graphify hook reference found.
)

if not defined GRAPHIFY_BIN goto CHECK_GRAPH

"%GRAPHIFY_BIN%" hook-check >nul 2>&1

if errorlevel 1 (
    echo [WARN] Direct hook-check failed.
    set "HAS_WARN=1"
) else (
    echo [PASS] Direct Graphify hook-check succeeds.
    echo        Hook is intentionally a NO-OP.
)

rem Verify default path used by the bundled hook.
if not exist "%USERPROFILE%\.local\bin\graphify.exe" (
    echo [WARN] User-local Graphify path not found.
    echo        Copied hook may require adaptation.
    set "HAS_WARN=1"
)

goto CHECK_GRAPH

:NO_HOOK

echo [WARN] Codex hooks.json missing.
echo        AGENTS.md can still control Graphify usage.
set "HAS_WARN=1"

rem ------------------------------------------------------------
rem Graph
rem ------------------------------------------------------------

:CHECK_GRAPH

echo.
echo ======================================================
echo GRAPH CHECKS
echo ======================================================

if not exist "graphify-out\graph.json" (
    echo [WARN] Project graph.json not found.
    set "HAS_WARN=1"
    goto GIT_CHECK
)

echo [PASS] Project graph.json found.

if not defined GRAPHIFY_BIN goto GIT_CHECK

echo.
echo [INFO] Running real Graphify query.
echo        Query may update an internal cache stamp.
echo.

"%GRAPHIFY_BIN%" query "What are the main components in this project?"

if errorlevel 1 (
    echo [WARN] Graphify query failed.
    echo        Retry with a known first-party symbol.
    set "HAS_WARN=1"
) else (
    echo [PASS] Graphify query command succeeded.
)

rem ============================================================
rem Git freshness evidence
rem ============================================================

:GIT_CHECK

echo.
echo ======================================================
echo GRAPH FRESHNESS EVIDENCE
echo ======================================================

where git.exe >nul 2>&1

if errorlevel 1 goto NO_GIT

git rev-parse --is-inside-work-tree >nul 2>&1

if errorlevel 1 goto NO_GIT

echo.
echo Git HEAD:
git rev-parse HEAD

echo.
echo Working tree:
git status --short

echo.
echo [NOTE] Matching Git HEAD and graph build commit
echo        does not prove full graph freshness.
echo.
echo        Indexed files may have uncommitted changes.
echo        Use incremental update when uncertain.

goto FINISH

:NO_GIT

echo [WARN] Git unavailable or project is not a worktree.
set "HAS_WARN=1"

rem ============================================================
rem Final report
rem ============================================================

:FINISH

echo.
echo ======================================================
echo SUMMARY
echo ======================================================
echo.
echo Setup package: "%KIT%"
echo Target project: "%CD%"
echo.
echo No Git hooks or file watchers were installed.
echo Global PATH and security settings were not edited.
echo Existing project instructions and skills were preserved.
echo Existing hooks.json was not overwritten.
echo Graphify hook-check is a NO-OP, not an updater.
echo.
echo Graph updates are AGENT-DRIVEN through AGENTS.md.
echo Codex must refresh the graph after code changes.
echo Manual skill selection should not be necessary.
echo.
echo Real automatic skill discovery and Codex hook
echo dispatch must be verified inside Codex itself.
echo.

if defined BACKUP_DIR (
    echo Configuration backup:
    echo "%BACKUP_DIR%"
    echo.
)

if "%HAS_FAIL%"=="1" goto FINISH_FAIL
if "%HAS_WARN%"=="1" goto FINISH_WARN

echo [PASS] Local Graphify integration checks passed.
echo.
popd
pause
exit /b 0

:FINISH_WARN

echo [WARN] Checks completed with warnings.
echo Review the messages above.
echo.
popd
pause
exit /b 1

:FINISH_FAIL

echo [FAIL] Setup or verification incomplete.
echo Review the messages above.
echo.
popd
pause
exit /b 2

rem ============================================================
rem Resolve Graphify CLI
rem ============================================================

:RESOLVE_GRAPHIFY

set "GRAPHIFY_BIN="

rem Prefer the known user-local uv installation.
if exist "%USERPROFILE%\.local\bin\graphify.exe" (
    set "GRAPHIFY_BIN=%USERPROFILE%\.local\bin\graphify.exe"
)

rem Fall back to Windows PATH.
if not defined GRAPHIFY_BIN (
    for /f "delims=" %%G in ('where graphify.exe 2^>nul') do if not defined GRAPHIFY_BIN set "GRAPHIFY_BIN=%%G"
)

if defined GRAPHIFY_BIN (
    if not exist "%GRAPHIFY_BIN%" set "GRAPHIFY_BIN="
)

exit /b 0

rem ============================================================
rem Resolve uv
rem ============================================================

:RESOLVE_UV

set "UV_BIN="

for /f "delims=" %%G in ('where uv.exe 2^>nul') do if not defined UV_BIN set "UV_BIN=%%G"

if not defined UV_BIN (
    if exist "%USERPROFILE%\.local\bin\uv.exe" (
        set "UV_BIN=%USERPROFILE%\.local\bin\uv.exe"
    )
)

if not defined UV_BIN (
    if exist "%LOCALAPPDATA%\Microsoft\WinGet\Links\uv.exe" (
        set "UV_BIN=%LOCALAPPDATA%\Microsoft\WinGet\Links\uv.exe"
    )
)

exit /b 0
