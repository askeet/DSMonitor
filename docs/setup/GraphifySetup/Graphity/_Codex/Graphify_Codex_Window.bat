
@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Portable Graphify + Codex CLI setup/check for Windows 10/11.
rem Keep this file OUTSIDE the project, next to AGENTS.md and optional .codex/ skill snapshot.
rem Never copy machine-specific hooks.json from the kit.
rem Usage: Graphify_Codex_Windows.bat --check "D:\Repositories\YourProject"
rem        Graphify_Codex_Windows.bat --setup "D:\Repositories\YourProject"

set "MODE=%~1"
set "PROJECT=%~2"
set "KIT=%~dp0"
set "GRAPHIFY_BIN="
set "UV_BIN="
set "BACKUP_DIR="
set "HAS_WARN=0"
set "HAS_FAIL=0"
rem Local-only Graphify invocation; do not select a paid LLM backend.
set "GRAPHIFY_BACKEND=none"
set "GRAPHIFY_QUERY_LOG_DISABLE=1"

if /I "%MODE%"=="--check" goto MODE_OK
if /I "%MODE%"=="--setup" goto MODE_OK
if not "%MODE%"=="" goto USAGE

echo.
echo Graphify for Codex CLI - Windows
choice /C 12 /M "Choose 1=CHECK or 2=SETUP"
if errorlevel 2 (set "MODE=--setup") else (set "MODE=--check")
goto MODE_OK

:USAGE
echo Usage: %~nx0 --check "D:\path\to\repository"
echo        %~nx0 --setup "D:\path\to\repository"
exit /b 2

:MODE_OK
if not defined PROJECT set /p "PROJECT=Project root folder (absolute path): "
if not defined PROJECT (
    echo [FAIL] A project path is required.
    exit /b 2
)
if not exist "%PROJECT%\." (
    echo [FAIL] Project folder not found: "%PROJECT%"
    exit /b 2
)
pushd "%PROJECT%" 2>nul
if errorlevel 1 (
    echo [FAIL] Cannot enter the selected project directory.
    exit /b 2
)
echo.
echo ======================================================
echo  Graphify for Codex CLI: %MODE%
echo  Target project: "%CD%"
echo  Setup kit: "%KIT%"
echo ======================================================
echo.

where git.exe >nul 2>&1
if errorlevel 1 (
    echo [WARN] Git not on PATH; freshness evidence will be limited.
    set "HAS_WARN=1"
) else (
    git rev-parse --is-inside-work-tree >nul 2>&1
    if errorlevel 1 (
        echo [WARN] Not inside a Git worktree. Confirm this is the project root.
        set "HAS_WARN=1"
    ) else (
        echo [INFO] Existing Git changes before setup/check:
        git status --short
    )
)

call :RESOLVE_GRAPHIFY
if defined GRAPHIFY_BIN goto HAVE_GRAPHIFY
if /I "%MODE%"=="--setup" goto NEED_GRAPHIFY
echo [FAIL] Graphify executable not found. Run --setup to request installation.
set "HAS_FAIL=1"
goto CHECK_INTEGRATION

:NEED_GRAPHIFY
echo [MISSING] Graphify CLI was not found on PATH or at the known uv location.
choice /C YN /M "Approve installing Graphify (graphifyy) with uv?"
if errorlevel 2 goto INSTALL_DECLINED
call :RESOLVE_UV
if defined UV_BIN goto INSTALL_GRAPHIFY

echo [MISSING] uv was not found.
where winget.exe >nul 2>&1
if errorlevel 1 (
    echo [FAIL] winget is unavailable. Install uv manually, then rerun --setup.
    set "HAS_FAIL=1"
    goto CHECK_INTEGRATION
)
choice /C YN /M "Approve installing uv using winget?"
if errorlevel 2 goto INSTALL_DECLINED
winget install --id astral-sh.uv --exact
if errorlevel 1 goto INSTALL_FAILURE
call :RESOLVE_UV
if not defined UV_BIN (
    echo [WARN] uv installed but not discoverable in this terminal.
    echo        Open a fresh PowerShell window and rerun --setup.
    set "HAS_FAIL=1"
    goto CHECK_INTEGRATION
)

:INSTALL_GRAPHIFY
echo [INFO] Installing PyPI package graphifyy via uv tool install.
"%UV_BIN%" tool install graphifyy
if errorlevel 1 goto INSTALL_FAILURE
call :RESOLVE_GRAPHIFY
if not defined GRAPHIFY_BIN (
    echo [FAIL] Installation succeeded but executable was not found.
    echo        Open a new terminal and rerun --setup.
    set "HAS_FAIL=1"
    goto CHECK_INTEGRATION
)
goto HAVE_GRAPHIFY

:INSTALL_FAILURE
echo [FAIL] Package installer returned an error.
set "HAS_FAIL=1"
goto CHECK_INTEGRATION

:INSTALL_DECLINED
echo [INFO] Package installation declined; nothing installed by this script.
set "HAS_FAIL=1"
goto CHECK_INTEGRATION

:HAVE_GRAPHIFY
echo [INFO] Using Graphify: "%GRAPHIFY_BIN%"
"%GRAPHIFY_BIN%" --version
if errorlevel 1 (
    echo [FAIL] Graphify executable failed its version check.
    set "HAS_FAIL=1"
    goto CHECK_INTEGRATION
)
if /I "%MODE%"=="--setup" goto SETUP_PROJECT
goto CHECK_INTEGRATION

:SETUP_PROJECT
call :SETUP_SKILL
call :SETUP_AGENTS
call :SETUP_GRAPH
goto CHECK_INTEGRATION

:SETUP_SKILL
if exist ".codex\skills\graphify\SKILL.md" (
    echo [PASS] Existing target Graphify skill is preserved.
    exit /b 0
)
if not exist "%KIT%.codex\skills\graphify\SKILL.md" goto SKILL_FROM_GRAPHIFY

echo [INFO] An optional Graphify skill snapshot exists in the setup kit.
choice /C YN /M "Copy ONLY Graphify skill and references into the target project?"
if errorlevel 2 (
    echo [WARN] User declined the bundled skill copy.
    set "HAS_WARN=1"
    exit /b 0
)
if not exist ".codex\skills" mkdir ".codex\skills" >nul 2>&1
xcopy "%KIT%.codex\skills\graphify" ".codex\skills\graphify" /E /I /Q /Y >nul
if errorlevel 1 (
    echo [FAIL] Skill snapshot copy failed; inspect the target folder.
    set "HAS_FAIL=1"
) else (
    if exist ".codex\skills\graphify\SKILL.md" (
        echo [PASS] Copied bundled Graphify skill without replacing other skills.
    ) else (
        echo [FAIL] No SKILL.md appeared after copying.
        set "HAS_FAIL=1"
    )
)
exit /b 0

:SKILL_FROM_GRAPHIFY
echo [INFO] No Graphify skill snapshot found in the setup kit.
echo        Official project-local installer can create it.
echo        It may edit AGENTS.md and .codex/hooks.json.
choice /C YN /M "Approve running graphify install --platform codex --project?"
if errorlevel 2 (
    echo [WARN] Project installer declined; Graphify skill remains absent.
    set "HAS_WARN=1"
    exit /b 0
)
call :BACKUP_CONFIG
if "%HAS_FAIL%"=="1" exit /b 1
"%GRAPHIFY_BIN%" install --platform codex --project
if errorlevel 1 (
    echo [FAIL] Official Graphify project integration returned an error.
    echo        Review files and the printed backup location.
    set "HAS_FAIL=1"
)
exit /b 0

:SETUP_AGENTS
if not exist "%KIT%AGENTS.md" (
    echo [FAIL] Template AGENTS.md missing from setup kit.
    set "HAS_FAIL=1"
    exit /b 1
)
findstr /I /C:"## Graphify" "%KIT%AGENTS.md" >nul 2>&1
if errorlevel 1 goto BAD_TEMPLATE
findstr /I /C:"graphify update" "%KIT%AGENTS.md" >nul 2>&1
if errorlevel 1 goto BAD_TEMPLATE
findstr /I /C:"graphify query" "%KIT%AGENTS.md" >nul 2>&1
if errorlevel 1 goto BAD_TEMPLATE

goto TEMPLATE_OK
:BAD_TEMPLATE
echo [FAIL] Kit AGENTS.md is incomplete. It must contain real Graphify rules.
set "HAS_FAIL=1"
exit /b 1

:TEMPLATE_OK
if exist "AGENTS.md" goto AGENTS_EXIST
choice /C YN /M "Create project AGENTS.md from the Graphify template?"
if errorlevel 2 (
    echo [WARN] AGENTS.md creation declined.
    set "HAS_WARN=1"
    exit /b 0
)
copy /Y "%KIT%AGENTS.md" "AGENTS.md" >nul
if errorlevel 1 (
    echo [FAIL] Could not create AGENTS.md.
    set "HAS_FAIL=1"
) else (
    echo [PASS] Created AGENTS.md with complete Graphify rules.
)
exit /b 0

:AGENTS_EXIST
findstr /I /C:"## Graphify" "AGENTS.md" >nul 2>&1
if not errorlevel 1 goto AGENTS_HAS_GRAPHIFY

echo [INFO] Existing AGENTS.md has no Graphify section.
choice /C YN /M "Append Graphify template rules without replacing existing instructions?"
if errorlevel 2 (
    echo [WARN] Graphify rules not appended.
    set "HAS_WARN=1"
    exit /b 0
)
call :BACKUP_CONFIG
if "%HAS_FAIL%"=="1" exit /b 1
>>"AGENTS.md" echo.
type "%KIT%AGENTS.md" >> "AGENTS.md"
if errorlevel 1 (
    echo [FAIL] Could not append Graphify template.
    set "HAS_FAIL=1"
) else (
    echo [PASS] Graphify section appended; unrelated instructions retained.
)
exit /b 0

:AGENTS_HAS_GRAPHIFY
rem A single heading is a safe-to-detect stub; ask before replacing that exact stub.
powershell -NoProfile -NonInteractive -Command "$s=[IO.File]::ReadAllText('AGENTS.md'); if($s.Trim() -eq '## Graphify'){exit 0}else{exit 1}" >nul 2>&1
if not errorlevel 1 goto AGENTS_ONLY_STUB
findstr /I /C:"graphify update" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INCOMPLETE
findstr /I /C:"graphify query" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INCOMPLETE
findstr /I /C:"Get-Command graphify" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INCOMPLETE
findstr /I /C:"delegated" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INCOMPLETE
echo [PASS] Existing complete Graphify instructions kept unchanged.
exit /b 0

:AGENTS_ONLY_STUB
echo [INFO] AGENTS.md contains ONLY the Graphify heading.
choice /C YN /M "Replace this empty stub with the complete Graphify template?"
if errorlevel 2 (
    echo [WARN] Stub not replaced.
    set "HAS_WARN=1"
    exit /b 0
)
call :BACKUP_CONFIG
if "%HAS_FAIL%"=="1" exit /b 1
copy /Y "%KIT%AGENTS.md" "AGENTS.md" >nul
if errorlevel 1 (
    echo [FAIL] Failed to replace AGENTS.md stub.
    set "HAS_FAIL=1"
) else (
    echo [PASS] Empty AGENTS.md stub replaced by complete rules.
)
exit /b 0

:AGENTS_INCOMPLETE
echo [WARN] AGENTS.md has Graphify instructions but lacks required rules.
echo        Merge the kit AGENTS.md section manually; other project rules are preserved.
set "HAS_WARN=1"
exit /b 0

:SETUP_GRAPH
if exist "graphify-out\graph.json" goto GRAPH_UPDATE_OFFER
echo [INFO] A new graph requires a local code-only extraction.
echo        Review project-specific vendor paths in .graphifyignore first.
choice /C YN /M "Build the code-only graph for this target project now?"
if errorlevel 2 (
    echo [WARN] Initial graph build skipped.
    set "HAS_WARN=1"
    exit /b 0
)
"%GRAPHIFY_BIN%" extract . --code-only
if errorlevel 1 (
    echo [FAIL] Graph extraction failed.
    set "HAS_FAIL=1"
) else (
    echo [PASS] Initial local graph extraction completed.
)
exit /b 0

:GRAPH_UPDATE_OFFER
choice /C YN /M "Existing graph found. Run Graphify incremental update?"
if errorlevel 2 (
    echo [INFO] Graph update skipped; freshness not established.
    set "HAS_WARN=1"
    exit /b 0
)
"%GRAPHIFY_BIN%" update .
if errorlevel 1 (
    echo [FAIL] Graphify incremental update failed.
    set "HAS_FAIL=1"
) else (
    echo [PASS] Incremental update command succeeded.
)
exit /b 0

:BACKUP_CONFIG
if defined BACKUP_DIR exit /b 0
set "BACKUP_DIR=%TEMP%\GraphifyCodexBackup_%RANDOM%_%RANDOM%"
mkdir "%BACKUP_DIR%" >nul 2>&1
if errorlevel 1 (
    echo [FAIL] Could not create a safe backup directory.
    set "HAS_FAIL=1"
    exit /b 1
)
if exist "AGENTS.md" copy /Y "AGENTS.md" "%BACKUP_DIR%\AGENTS.md" >nul
if exist ".codex\hooks.json" copy /Y ".codex\hooks.json" "%BACKUP_DIR%\hooks.json" >nul
echo [INFO] Pre-change backups: "%BACKUP_DIR%"
exit /b 0

:CHECK_INTEGRATION
echo.
echo ---------- Codex project configuration ----------
where codex.cmd >nul 2>&1
if not errorlevel 1 goto CODEX_OK
where codex.exe >nul 2>&1
if not errorlevel 1 goto CODEX_OK
echo [WARN] Codex launcher not found in PATH. Codex may still be available via desktop app.
set "HAS_WARN=1"
goto CHECK_SKILL
:CODEX_OK
echo [PASS] Codex CLI launcher found.
:CHECK_SKILL
if exist ".codex\skills\graphify\SKILL.md" (
    echo [PASS] Project-local Graphify SKILL.md exists.
) else (
    echo [FAIL] Missing .codex\skills\graphify\SKILL.md
    set "HAS_FAIL=1"
)
if not exist "AGENTS.md" goto AGENTS_MISSING
findstr /I /C:"## Graphify" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INSUFFICIENT
findstr /I /C:"graphify update" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INSUFFICIENT
findstr /I /C:"graphify query" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INSUFFICIENT
findstr /I /C:"Get-Command graphify" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INSUFFICIENT
findstr /I /C:"delegated" "AGENTS.md" >nul 2>&1
if errorlevel 1 goto AGENTS_INSUFFICIENT
echo [PASS] AGENTS.md includes queries, updates, delegation and Windows CLI fallback.
goto CHECK_HOOK
:AGENTS_MISSING
echo [FAIL] Missing AGENTS.md
goto AGENTS_FAILURE
:AGENTS_INSUFFICIENT
echo [FAIL] Graphify section missing or incomplete in AGENTS.md
:AGENTS_FAILURE
set "HAS_FAIL=1"

:CHECK_HOOK
if not exist ".codex\hooks.json" goto NO_HOOK
powershell -NoProfile -NonInteractive -Command "try { $null = ConvertFrom-Json -InputObject ([IO.File]::ReadAllText('.codex\hooks.json')); exit 0 } catch { exit 1 }" >nul 2>&1
if errorlevel 1 (
    echo [WARN] .codex\hooks.json is invalid JSON; inspect manually.
    set "HAS_WARN=1"
) else (
    echo [INFO] Existing hooks.json parses as JSON; its dispatch is unverified.
)
if not defined GRAPHIFY_BIN goto CHECK_GRAPH
"%GRAPHIFY_BIN%" hook-check >nul 2>&1
if errorlevel 1 (
    echo [WARN] Direct hook-check did not exit successfully.
    set "HAS_WARN=1"
) else (
    echo [PASS] Direct hook-check runs; this is a no-op, not auto-update.
)
where graphify.exe >nul 2>&1
if not errorlevel 1 goto CHECK_GRAPH
findstr /I /C:"graphify hook-check" ".codex\hooks.json" >nul 2>&1
if errorlevel 1 goto CHECK_GRAPH
echo [WARN] hooks.json may use bare graphify, unavailable in this PATH.
echo        Do not copy machine-specific hook paths from other computers.
set "HAS_WARN=1"
goto CHECK_GRAPH
:NO_HOOK
echo [INFO] No hooks.json. It is OPTIONAL: Codex no-op hook is not an updater.

:CHECK_GRAPH
echo.
echo ---------- Graph and query checks ----------
if not exist "graphify-out\graph.json" (
    echo [WARN] graphify-out\graph.json missing.
    set "HAS_WARN=1"
    goto GIT_CHECK
)
echo [PASS] Project graph.json found.
if not defined GRAPHIFY_BIN goto GIT_CHECK
echo [INFO] Running a real Graphify query; a query-cache stamp may be written.
"%GRAPHIFY_BIN%" query "What are the main components in this project?"
if errorlevel 1 (
    echo [WARN] Query failed; try again with a real first-party symbol.
    set "HAS_WARN=1"
) else (
    echo [PASS] Graphify query command exited successfully.
)

:GIT_CHECK
echo.
echo ---------- Git freshness evidence ----------
where git.exe >nul 2>&1
if errorlevel 1 goto NO_GIT
git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 goto NO_GIT
echo HEAD:
git rev-parse HEAD
echo Working tree:
git status --short
echo [NOTE] Equal build commit and HEAD do not prove a fresh graph.
echo        Indexed inputs can change without a new commit.
goto FINISH
:NO_GIT
echo [WARN] Git unavailable or not a worktree.
set "HAS_WARN=1"

:FINISH
echo.
echo ---------- Summary ----------
echo The script does not install Git hooks, file watchers, or change global PATH.
echo No app code/tests or commits are intentionally modified.
echo Copied setup-kit hooks.json is NEVER installed: it can contain another user's path.
echo Graph maintenance is AGENT-DRIVEN by AGENTS.md; a Codex no-op hook is not a refresh.
echo Codex skill discovery and real hook dispatch must be verified in Codex itself.
if defined BACKUP_DIR echo Configuration backup: "%BACKUP_DIR%"
if "%HAS_FAIL%"=="1" goto FINISH_FAIL
if "%HAS_WARN%"=="1" goto FINISH_WARN

echo [PASS] Local CLI and project integration checks passed.
popd
pause
exit /b 0

:FINISH_WARN
echo [WARN] Completed with warnings, see messages above.
popd
pause
exit /b 1

:FINISH_FAIL
echo [FAIL] Setup/check incomplete, see messages above.
popd
pause
exit /b 2

:RESOLVE_GRAPHIFY
set "GRAPHIFY_BIN="
if exist "%USERPROFILE%\.local\bin\graphify.exe" set "GRAPHIFY_BIN=%USERPROFILE%\.local\bin\graphify.exe"
for /f "delims=" %%G in ('where graphify.exe 2^>nul') do if not defined GRAPHIFY_BIN set "GRAPHIFY_BIN=%%G"
if defined GRAPHIFY_BIN if not exist "%GRAPHIFY_BIN%" set "GRAPHIFY_BIN="
exit /b 0

:RESOLVE_UV
set "UV_BIN="
for /f "delims=" %%G in ('where uv.exe 2^>nul') do if not defined UV_BIN set "UV_BIN=%%G"
if not defined UV_BIN if exist "%USERPROFILE%\.local\bin\uv.exe" set "UV_BIN=%USERPROFILE%\.local\bin\uv.exe"
if not defined UV_BIN if exist "%LOCALAPPDATA%\Microsoft\WinGet\Links\uv.exe" set "UV_BIN=%LOCALAPPDATA%\Microsoft\WinGet\Links\uv.exe"
exit /b 0
