@echo off
setlocal EnableExtensions DisableDelayedExpansion
rem Graphify + Codex project-local setup/check for Windows 10/11.
rem No silent installs, no PATH/security changes, no Git hook installations.
rem Usage: Graphify_Codex_Windows.bat --check "C:\path\to\repo"
rem        Graphify_Codex_Windows.bat --setup "C:\path\to\repo"

set "MODE=%~1"
set "PROJECT=%~2"
set "GRAPHIFY_BIN="
set "UV_BIN="
set "HAS_WARN=0"
set "HAS_FAIL=0"
rem Process-local safeguard: do not allow semantic extraction to choose a paid provider.
set "GRAPHIFY_BACKEND=none"
set "GRAPHIFY_QUERY_LOG_DISABLE=1"

if /I "%MODE%"=="--check" goto MODE_OK
if /I "%MODE%"=="--setup" goto MODE_OK
if not "%MODE%"=="" goto USAGE

echo.
echo Graphify for Codex CLI - Windows
choice /C 12 /M "Choose 1=CHECK existing setup or 2=SETUP project"
if errorlevel 2 (set "MODE=--setup") else (set "MODE=--check")
goto MODE_OK

:USAGE
echo Usage: %~nx0 --check "D:\path\to\repository"
echo        %~nx0 --setup "D:\path\to\repository"
exit /b 2

:MODE_OK
if not defined PROJECT set /p "PROJECT=Project root folder (absolute path): "
if not defined PROJECT (
    echo [FAIL] No project path supplied.
    exit /b 2
)
if not exist "%PROJECT%\." (
    echo [FAIL] Folder not found: "%PROJECT%"
    exit /b 2
)
pushd "%PROJECT%" 2>nul
if errorlevel 1 (
    echo [FAIL] Cannot enter project directory.
    exit /b 2
)

echo.
echo =======================================================
echo  Graphify / Codex  %MODE%
echo  Repository: %CD%
echo =======================================================
echo.

if exist ".git" goto REPO_OK
where git.exe >nul 2>&1
if errorlevel 1 goto REPO_WARN
git rev-parse --show-toplevel >nul 2>&1
if not errorlevel 1 goto REPO_OK
:REPO_WARN
echo [WARN] No Git worktree identified at this location.
echo        Ensure this is the intended repository root.
set "HAS_WARN=1"
:REPO_OK

call :RESOLVE_GRAPHIFY
if defined GRAPHIFY_BIN goto HAVE_GRAPHIFY
if /I "%MODE%"=="--setup" goto NEED_GRAPHIFY
echo [FAIL] Graphify executable not found.
echo        See the Markdown guide for approved installation options.
set "HAS_FAIL=1"
goto CHECK_INTEGRATION

:NEED_GRAPHIFY
echo [MISSING] Graphify CLI not found on PATH or usual uv location.
choice /C YN /M "Install Graphify (graphifyy) with uv, with approval?"
if errorlevel 2 goto INSTALL_DECLINED
call :RESOLVE_UV
if defined UV_BIN goto INSTALL_GRAPHIFY

echo [MISSING] uv executable not found.
where winget.exe >nul 2>&1
if errorlevel 1 goto NO_WINGET
choice /C YN /M "Install uv via winget now, with approval?"
if errorlevel 2 goto INSTALL_DECLINED
winget install --id astral-sh.uv --exact
if errorlevel 1 goto INSTALL_FAILURE
call :RESOLVE_UV
if not defined UV_BIN goto UV_RESTART_REQUIRED
goto INSTALL_GRAPHIFY

:NO_WINGET
echo [FAIL] winget not found. Install uv manually, then run this script again.
set "HAS_FAIL=1"
goto CHECK_INTEGRATION

:UV_RESTART_REQUIRED
echo [WARN] uv installed but not yet discoverable in this shell.
echo        Open a NEW PowerShell or CMD window and rerun --setup.
set "HAS_FAIL=1"
goto CHECK_INTEGRATION

:INSTALL_GRAPHIFY
echo [INFO] Installing the official PyPI package graphifyy using uv.
"%UV_BIN%" tool install graphifyy
if errorlevel 1 goto INSTALL_FAILURE
call :RESOLVE_GRAPHIFY
if defined GRAPHIFY_BIN goto HAVE_GRAPHIFY
echo [WARN] Graphify installed, but executable not found in this shell.
echo        Open a new terminal, then rerun this script.
set "HAS_FAIL=1"
goto CHECK_INTEGRATION

:INSTALL_FAILURE
echo [FAIL] Installer returned an error. No global PATH edits were made by this script.
set "HAS_FAIL=1"
goto CHECK_INTEGRATION

:INSTALL_DECLINED
echo [INFO] Installation declined. No packages installed by this script.
set "HAS_FAIL=1"
goto CHECK_INTEGRATION

:HAVE_GRAPHIFY
echo [PASS] Graphify executable: "%GRAPHIFY_BIN%"
"%GRAPHIFY_BIN%" --version
if errorlevel 1 (
    echo [FAIL] Graphify --version failed.
    set "HAS_FAIL=1"
    goto CHECK_INTEGRATION
)

if /I "%MODE%"=="--setup" goto MAYBE_INSTALL_SKILL
goto CHECK_INTEGRATION

:MAYBE_INSTALL_SKILL
if exist ".codex\skills\graphify\SKILL.md" (
    echo [PASS] Existing project-local Codex skill retained.
    goto MAYBE_BUILD
)
echo [INFO] Project integration may write AGENTS.md and .codex files.
choice /C YN /M "Approve project-local Codex integration now?"
if errorlevel 2 (
    echo [WARN] Integration was not authorized.
    set "HAS_WARN=1"
    goto MAYBE_BUILD
)
rem Back up files the upstream installer may modify. Never overwrite an old backup.
set "BACKUP_DIR=%TEMP%\GraphifyCodexBackup_%RANDOM%_%RANDOM%"
mkdir "%BACKUP_DIR%" >nul 2>&1
if exist "AGENTS.md" copy /Y "AGENTS.md" "%BACKUP_DIR%\AGENTS.md" >nul
if exist ".codex\hooks.json" copy /Y ".codex\hooks.json" "%BACKUP_DIR%\hooks.json" >nul
echo [INFO] Pre-install backups, if files existed: "%BACKUP_DIR%"
"%GRAPHIFY_BIN%" install --platform codex --project
if errorlevel 1 (
    echo [FAIL] Graphify project integration failed. Inspect files and backup.
    set "HAS_FAIL=1"
)

:MAYBE_BUILD
if exist "graphify-out\graph.json" goto MAYBE_UPDATE
choice /C YN /M "No graph found. Build a local code-only graph now?"
if errorlevel 2 goto SKIP_BUILD
"%GRAPHIFY_BIN%" extract . --code-only
if errorlevel 1 (
    echo [FAIL] Code-only extraction failed.
    set "HAS_FAIL=1"
)
goto CHECK_INTEGRATION

:MAYBE_UPDATE
choice /C YN /M "Existing graph found. Run an incremental update now?"
if errorlevel 2 goto SKIP_BUILD
"%GRAPHIFY_BIN%" update .
if errorlevel 1 (
    echo [FAIL] Incremental update failed.
    set "HAS_FAIL=1"
)
goto CHECK_INTEGRATION

:SKIP_BUILD
echo [INFO] Graph build/update skipped by user.

:CHECK_INTEGRATION
echo.
echo ---------- Project integration checks ----------
where codex.cmd >nul 2>&1
if not errorlevel 1 goto CODEX_FOUND
where codex.exe >nul 2>&1
if not errorlevel 1 goto CODEX_FOUND
echo [WARN] Codex CLI command not found on PATH. Verify Codex separately.
set "HAS_WARN=1"
goto CODEX_CHECK_DONE
:CODEX_FOUND
echo [PASS] Codex CLI launcher found on PATH.
:CODEX_CHECK_DONE
if exist ".codex\skills\graphify\SKILL.md" (
    echo [PASS] .codex\skills\graphify\SKILL.md
) else (
    echo [FAIL] Codex skill missing in this project.
    set "HAS_FAIL=1"
)
if exist "AGENTS.md" goto CHECK_AGENTS
echo [WARN] AGENTS.md missing: auto-use instructions not available.
set "HAS_WARN=1"
goto CHECK_HOOK
:CHECK_AGENTS
findstr /I /C:"Graphify" "AGENTS.md" >nul 2>&1
if errorlevel 1 (
    echo [WARN] AGENTS.md exists but no Graphify guidance found.
    set "HAS_WARN=1"
) else (
    echo [PASS] Graphify mentioned in AGENTS.md.
)
:CHECK_HOOK
if exist ".codex\hooks.json" goto HOOK_PRESENT
echo [INFO] .codex\hooks.json absent. Codex guidance can still use AGENTS.md.
goto CHECK_GRAPH
:HOOK_PRESENT
echo [INFO] Codex hooks.json present. Hook dispatch is NOT verified here.
if not defined GRAPHIFY_BIN goto CHECK_GRAPH
"%GRAPHIFY_BIN%" hook-check >nul 2>&1
if errorlevel 1 (
    echo [WARN] Direct Graphify hook-check failed.
    set "HAS_WARN=1"
) else (
    echo [PASS] Direct hook-check exits successfully; this is a NO-OP.
)
where graphify.exe >nul 2>&1
if not errorlevel 1 goto CHECK_GRAPH
findstr /I /C:"graphify hook-check" ".codex\hooks.json" >nul 2>&1
if errorlevel 1 goto CHECK_GRAPH
echo [WARN] Hook file may call bare graphify, but graphify is absent from PATH.
echo        Direct CLI fallback works; Codex hook dispatch may not.
set "HAS_WARN=1"

:CHECK_GRAPH
echo.
echo ---------- Graph checks ----------
if exist "graphify-out\graph.json" goto GRAPH_PRESENT
echo [FAIL] graphify-out\graph.json missing. Use --setup to build it.
set "HAS_FAIL=1"
goto GIT_CHECK
:GRAPH_PRESENT
echo [PASS] graphify-out\graph.json exists.
if not defined GRAPHIFY_BIN goto GIT_CHECK
echo [INFO] Running one real scoped CLI query. A query can write a cache stamp.
"%GRAPHIFY_BIN%" query "What are the main modules in this codebase?"
if errorlevel 1 (
    echo [WARN] Generic query returned a nonzero code; retry with a known symbol.
    set "HAS_WARN=1"
) else (
    echo [PASS] Graphify query succeeded.
)

:GIT_CHECK
echo.
echo ---------- Freshness evidence ----------
where git.exe >nul 2>&1
if errorlevel 1 goto NO_GIT

git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 goto NO_GIT
echo Git HEAD:
git rev-parse --short HEAD
echo Git worktree changes:
git status --short
echo [NOTE] Matching graph build commit and HEAD alone NEVER proves freshness.
echo        Compare Graphify manifest/input hashes and run update when unsure.
goto FINISH
:NO_GIT
echo [WARN] Git unavailable or this directory is not a worktree.
set "HAS_WARN=1"

:FINISH
echo.
echo ---------- Summary ----------
echo Changes to existing production code/tests: NONE by this script.
echo Git hooks / background watchers / global PATH: NOT installed or modified.
echo Codex PreToolUse no-op hook does NOT auto-refresh the graph.
echo Actual Codex-triggered skill/hook dispatch: NOT tested by this batch file.
echo To keep graph fresh, agents must run Graphify update after code changes.
if "%HAS_FAIL%"=="1" goto FINISH_FAIL
if "%HAS_WARN%"=="1" goto FINISH_WARN
echo [PASS] Local integration/CLI checks passed. Freshness still needs evidence.
popd
exit /b 0
:FINISH_WARN
echo [WARN] Checks completed with warnings. See messages above.
popd
exit /b 1
:FINISH_FAIL
echo [FAIL] Setup/check is incomplete. See messages above.
popd
exit /b 2

:RESOLVE_GRAPHIFY
set "GRAPHIFY_BIN="
rem Prefer a known user-tool location over a same-named executable in a repo.
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
