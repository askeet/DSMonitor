# Graphify for Codex and GitHub Copilot on Windows

**One reusable setup folder, any number of projects.** Use this guide on Windows 10/11, including new machines. Tested behavior is based on Graphify **0.9.112**; later versions may change their installer or CLI syntax.

## 1. Prepare the shared setup folder

Keep the package **outside** your repositories, for example at `D:\Tools\GraphifySetup`:

```text
GraphifySetup/
├── Graphify_Codex_Windows.bat   # Run this to check or set up a project
├── Graphify_Codex_Setup.md      # This guide; keep it in the setup folder
├── Merge-ProjectInstructions.ps1 # Used by the BAT for both instruction files
├── AGENTS.md                    # Graphify and Project Map sections for Codex
├── .github/copilot-instructions.md # Graphify and Project Knowledge sections
├── .graphifyignore              # Optional ignore template
└── .codex/
    ├── hooks.json               # Optional hook template
    └── skills/graphify/         # Optional: copy from a trusted existing installation
        ├── SKILL.md
        └── references/
```

**Do not copy the BAT or this guide into each project.** Do not copy an entire `.codex/` folder indiscriminately. If you want the offline skill snapshot, copy **only** `.codex/skills/graphify/` from your trusted Anki setup into this shared folder; preserve the `references/` subfolder.

An old `.codex/hooks.json` may contain an absolute path such as `C:/Users/<old-user>/.local/bin/graphify.exe`, so **do not reuse it** across machines. The BAT copies the bundled user-relative hook only when the target hook file is missing. The Codex hook is optional because `graphify hook-check` is a **no-op**, not an update trigger.

Keep both instruction templates and the PowerShell merge helper next to the BAT in the shared kit. The BAT runs the helper with PowerShell 7 when available and Windows PowerShell otherwise, under the machine's existing execution policy. Graphify CLI details live in the Codex skill.

## 2. Check or set up any project

Open PowerShell. **Keep the setup package where it is** and supply the **target repository path**:

```powershell
# Inspect an existing integration (no package installations or intentional code edits):
& "D:\Tools\GraphifySetup\Graphify_Codex_Windows.bat" --check "D:\Repositories\Python\YourProject"

# Set up a new project, or finish an incomplete setup:
& "D:\Tools\GraphifySetup\Graphify_Codex_Windows.bat" --setup "D:\Repositories\Python\YourProject"
```

Or double-click the BAT: it asks for the mode and the project's root path. **Use the repository root**, not a nested source folder. Quoting paths handles spaces.

`--setup` offers each missing instruction section separately and asks before installing packages, updating an existing skill, or extracting/updating a graph. It never commits files, changes system security settings or global PATH, installs Git hooks, or sets up background services. `--check` may write a benign Graphify query-cache timestamp because it executes a real query.

### What the BAT actually does

| Step | `--check` | `--setup` |
|---|---|---|
| Find Graphify CLI | Check PATH and `%USERPROFILE%\.local\bin\graphify.exe` | If missing, ask before installing `uv`/`graphifyy` |
| Check Codex CLI | Warn if not on PATH | Same |
| Graphify skill | Confirm the installed skill and check its version | Copy the bundled skill or offer to update and back up an existing copy |
| Project instructions | Verify both expected sections in each file, including their skill or map references | Offer to append each missing section independently, including when creating a file |
| Existing sections | Report duplicate headings or missing references | Preserve existing sections and report issues for manual review |
| .codex/hooks.json | Validate JSON and try direct hook-check | Copy the bundled template if missing and preserve existing hooks |
| Graph | Confirm `graphify-out/graph.json` and run a query | Offer code-only initial `extract` or incremental `update` |
| Git | Show HEAD and working changes | Show status before/after; **no commit** |

Before appending to an existing instruction file, the merger saves a byte-for-byte backup under %TEMP% and prints the path. Declining a section leaves it absent. Repeating setup leaves existing sections and unrelated content intact. Review any incomplete or duplicate sections manually.

Codex uses the Project Map section of AGENTS.md. Copilot uses its Project Knowledge section, which refers back to those shared rules. Agents create or maintain docs/AI/PROJECT_MAP.md from project-specific evidence when writes are permitted. The setup kit does not supply or generate that document.

### What goes into the target repository

```text
YourProject/
├── .codex/
│   ├── skills/graphify/SKILL.md
│   └── hooks.json                # Optional; copied only when missing
├── AGENTS.md                    # Graphify and Project Map sections
├── .github/copilot-instructions.md # Graphify and Project Knowledge sections
├── .graphifyignore              # Optional, tailor to THIS repository
└── graphify-out/                # Locally generated project-specific graph
```

Do **not** copy `graphify-out/` or `.graphifyignore` from the Anki project to unrelated repositories. Review actual vendored/generated directories before writing exclusions. A Python add-on, WPF app and Vue project have different source and dependencies.

## 3. Verify the real Graphify executable

When `graphify` is missing from Codex's PATH, a Windows `uv tool` installation may still be healthy. In a project PowerShell terminal:

```powershell
$graphifyExe = (Get-Command graphify -ErrorAction SilentlyContinue).Source
if (-not $graphifyExe) { $graphifyExe = Join-Path $env:USERPROFILE '.local\bin\graphify.exe' }
& $graphifyExe --version
& $graphifyExe query 'What are the main components in this project?'
```

In the tested environment, the package is `graphifyy` (two `y`s), and the CLI is `graphify`. If `uv` itself is missing, the setup script can **ask** to install it via `winget`; if installs are declined, it stops without installing anything. The script does **not** attempt `python -m graphify` or modify global PATH.

A successful query proves the graph is accessible, **not necessarily current**. The Graphify skill instructs Codex to compare the graph's build/input evidence with Git HEAD and working changes and run `graphify update .` when needed.

## 4. Confirm automatic use in Codex CLI

Start a **new** Codex CLI session in the target repository. Do **not** select the Graphify skill manually for this test. Send:

> Use the project's `AGENTS.md` Graphify instructions to identify dependencies for one real first-party symbol. Verify graph freshness before trusting results, run a real Graphify query (including the Windows executable fallback when necessary), confirm important paths in source, and report the exact commands and exit codes. Read-only; no installs or code changes.

A good report shows that the skill was recognized **and** a real CLI query ran. If it reports `graphify` unavailable, verify it tested `%USERPROFILE%\.local\bin\graphify.exe`, not just PATH. A `PreToolUse` `hook-check` returning exit code 0 does **not** prove the agent used the graph or refreshed it.

After a real code change, Codex should execute `graphify update .`, whether it wrote the change directly or a delegated worker did. This update is **instruction-driven** rather than guaranteed by an operating-system watcher or Git hook.

## 5. Review configuration before committing

From the **target project** root:

```powershell
git status --short
git diff -- AGENTS.md .github/copilot-instructions.md .codex/hooks.json
git diff --check
```

Also inspect **untracked** `.codex` skill files. Review the output from `--check` for incomplete instructions or a stale machine-specific hook. Do not commit `graphify-out/` or absolute hook paths without deciding an intentional repository policy.

Exit codes from the BAT: `0` = local checks passed (graph freshness and Codex dispatch still not guaranteed), `1` = warnings, `2` = setup/check incomplete. The setup is ready for routine use after a real CLI query succeeds and Codex demonstrates its automatic use.

## Troubleshooting

| Symptom | Action |
|---|---|
| `graphify` not on PATH | Use `%USERPROFILE%\.local\bin\graphify.exe`; no reinstall needed if it works |
| Kit `.codex/` is empty | Restore the bundled skill files in the shared setup kit |
| `AGENTS.md` has only `## Graphify` | Replace **only that stub** with the kit's complete section, preserving other rules |
| Existing `AGENTS.md` already has unrelated rules | Let script append, or manually merge the template; never overwrite |
| Stale hardcoded path inside `hooks.json` | Inspect and correct only that Graphify hook path; hook itself is optional/no-op |
| Graph missing | With approval: `graphify extract . --code-only` |
| Graph freshness uncertain | `graphify update .`; compare recorded inputs, not just commit IDs |
| First-party `.html` missing from graph | Code-only mode may omit it; inspect source directly |

**Official sources:** [Graphify repository](https://github.com/Graphify-Labs/graphify) · [uv installation](https://docs.astral.sh/uv/getting-started/installation/)
