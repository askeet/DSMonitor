# Graphify + Codex CLI on Windows: install, verify, maintain

Portable instructions for **Windows 10/11**, a **new computer** or an **existing repository**. Works per repository, including Python, C#, and other Graphify-supported source languages. The included `Graphify_Codex_Windows.bat` is an interactive **check/setup** helper.

> **Safety first:** The script never silently installs packages. It asks before installing `uv` or `graphifyy`, before modifying the repository through `graphify install`, and before building/updating a graph. It does **not** install Git hooks, file watchers, services, change the global PATH/security settings, commit files, or run project tests. It sets `GRAPHIFY_BACKEND=none` **only in the script process** to avoid accidental LLM-backed extraction. Inspect the upstream install output and Git diff before committing. The generated Codex `PreToolUse` hook is not a freshness guarantee.

## 1. Use the BAT file

Copy `Graphify_Codex_Windows.bat` onto the Windows machine. Open **PowerShell** or **Command Prompt** and run one of these commands. Quote the repository path, especially if it contains spaces.

```powershell
# Audit an existing installation. Does NOT install or rebuild a graph.
& ".\Graphify_Codex_Windows.bat" --check "D:\Repositories\Python\vocabthread-anki"

# Setup a new project, or complete an existing partial setup.
& ".\Graphify_Codex_Windows.bat" --setup "D:\Repositories\WPF\DSMonitor"
```

You can also double-click the BAT file. It will ask for `CHECK` versus `SETUP` and for a project path.

**What the BAT checks**

| Area | Evidence | Important limit |
|---|---|---|
| CLI | `graphify.exe --version` via PATH or `%USERPROFILE%\.local\bin\graphify.exe` | No assumed global PATH change |
| Codex CLI | `codex` launcher discoverable on PATH | Does not log in or install Codex |
| Project skill | `.codex/skills/graphify/SKILL.md` exists | True auto-discovery still needs an interactive Codex session |
| Project instructions | `AGENTS.md` mentions Graphify | Does not prove the agent follows it |
| Hook | Direct `graphify hook-check` succeeds if a hook config exists | **No-op**; does not check or refresh the graph; Codex dispatch not tested |
| Graph | `graphify-out/graph.json` exists; real `graphify query` attempted | Generic query may need replacement with a known symbol; query may write a cache timestamp |
| Git | Prints `HEAD` and `git status --short` | **Matching HEAD does not prove freshness**; uncommitted code matters |

Exit codes: **0** = local checks passed (freshness not guaranteed), **1** = warnings, **2** = incomplete/failed. `--check` can create a Graphify query cache stamp but makes no intentional application-code changes.

## 2. Manual installation on a new computer (alternative to BAT)

Graphify's official PyPI package is **`graphifyy`**, with **two `y` characters**. The CLI command is `graphify`.

First ensure **Codex CLI** and **Git** are already installed separately if needed. This guide does not alter their global configuration. Install `uv` using the official Windows package manager (only with your explicit approval):

```powershell
winget install --id astral-sh.uv --exact
```

Close and reopen PowerShell, then install Graphify in its own tool environment:

```powershell
uv --version
uv tool install graphifyy
graphify --version
```

If the bare `graphify` command is not recognized, **do not reinstall immediately**. On uv installations, try:

```powershell
$graphifyExe = Join-Path $env:USERPROFILE '.local\bin\graphify.exe'
& $graphifyExe --version
```

If this works, use `& $graphifyExe` for subsequent commands. You may run `uv tool update-shell` if you **choose** to adjust your user shell PATH; it is not required for this workflow and the BAT does not run it. Avoid `python -m graphify` as a fallback for an isolated `uv tool` installation.

### Initialize **one repository**

```powershell
cd "D:\path\to\your repository"
$graphifyExe = (Get-Command graphify -ErrorAction SilentlyContinue).Source
if (-not $graphifyExe) { $graphifyExe = Join-Path $env:USERPROFILE '.local\bin\graphify.exe' }
& $graphifyExe --version

# Inspect any existing uncommitted changes before setup.
git status --short

# Creates/updates project-scoped skill/instructions; review its diff.
& $graphifyExe install --platform codex --project

# Initial local code-only index, avoiding semantic API calls:
$env:GRAPHIFY_BACKEND = 'none'
& $graphifyExe extract . --code-only
```

The installer may change `AGENTS.md`, `.codex/skills/graphify/`, and `.codex/hooks.json`. If an existing skill is already installed, **prefer checking it instead of reinstalling**. Preserve custom agent/delegation instructions, local edits, and relevant Project maps. For code-only graphs, documents and some UI markup (for example, `.html`) can be omitted; source inspection remains necessary for those areas.

### Ignore **actual** third-party code, not first-party code

Before the first extraction, inspect vendored/generated directories and use project-local `.graphifyignore` patterns only where justified. Example **for a repo that really vendors these folders**:

```gitignore
# Example only; adapt to your project's actual structure.
libs/
connectors/edge_tts/_vendor/
```

Do **not** copy these exclusions into unrelated repositories unless those paths truly contain bundled dependencies. After changing ignores, rebuild or incrementally update as appropriate and confirm first-party code is indexed. Avoid `--no-gitignore` by default.

## 3. Verify CLI and graph in PowerShell

From the project root:

```powershell
$graphifyExe = (Get-Command graphify -ErrorAction SilentlyContinue).Source
if (-not $graphifyExe) { $graphifyExe = Join-Path $env:USERPROFILE '.local\bin\graphify.exe' }
& $graphifyExe --version

Test-Path .\.codex\skills\graphify\SKILL.md
Test-Path .\AGENTS.md
Test-Path .\graphify-out\graph.json

# Use a real symbol or a realistic question from THIS project.
& $graphifyExe query 'Which modules depend on template_model.plan_field?'
# Example only for the Python Anki project; use an actual symbol elsewhere.

# This command is a no-op in the observed Graphify 0.9.112 setup.
& $graphifyExe hook-check
```

A query returning relevant nodes confirms a **usable** graph, **not that it is fresh**. A zero exit from `hook-check` also confirms **nothing about freshness or Codex-triggered hook dispatch**. For repositories whose language has incomplete Graphify coverage, verify results against source and report omissions.

### Freshness and incremental updates

Graphify records a build commit in graph metadata/report. Compare that with the working tree and **indexed source files**. `git status --short` identifies uncommitted changes and `git diff --name-status <build-commit>` identifies committed differences, but neither alone certifies hash-level graph freshness. Build commits may match when uncommitted files changed after extraction.

```powershell
git rev-parse HEAD
git status --short

# After code edits, delegated edits, pull/merge or branch switch when source may differ:
$env:GRAPHIFY_BACKEND = 'none'
& $graphifyExe update .
```

On a newly initialized project without `graphify-out/graph.json`, use `extract . --code-only`, **not** `update .` as a substitute for initial indexing. `graphify check-update .` checks a non-code pending marker; it is **not** a general replacement for freshness checks.

**Automatic updates are NOT enforced by the Codex no-op PreToolUse hook.** The integration is instruction-driven: Codex is expected to run the update, and a report must show the actual command and result. Git hooks and file watchers are separate optional mechanisms and are intentionally **not** set up by this guide.

## 4. Recommended **AGENTS.md** rules for every repository

The project's existing `AGENTS.md` remains authoritative for architecture, safety, model choice and delegation. Review what Graphify's installer added; if it lacks a clear rule, add or adapt **one concise Graphify section** without duplicating existing policies:

```markdown
## Graphify

- Use the repository-local Graphify skill automatically for nontrivial codebase navigation, dependency and impact analysis; explicit selection is optional. For routine documentation edits or trivial isolated tasks, no graph query is needed.
- Before relying on the graph, check `graphify-out/graph.json`, its recorded build commit and input freshness, `git rev-parse HEAD`, and relevant uncommitted/changed source. If uncertain or stale, run an incremental `graphify update .` before dependency analysis; do not assume that equal commits mean freshness.
- Prefer scoped `graphify query`, `graphify explain`, `graphify path`, or `graphify affected` over loading the entire graph. Verify important relationships in actual source; continue from source when Graphify lacks coverage (for example HTML or markup not indexed by code-only mode).
- On Windows, resolve the executable with `Get-Command graphify`; if unavailable, try `& "$env:USERPROFILE\.local\bin\graphify.exe"`. Use the resolved executable for **all** Graphify subcommands. Do not treat a PATH miss as proof that Graphify is missing; do not try `python -m graphify` or reinstall it without approval.
- After any successful changes to indexed code, whether performed directly by Codex or by a delegated implementation worker, the coordinator/responsible Codex agent runs `graphify update .` before further graph-based analysis or final QA. The implementation worker need not maintain the graph. Reuse incremental updates; avoid needless full rebuilds. After Git updates, recheck freshness.
- When a task modifies code, final report: `Graphify: UPDATED`, `UNCHANGED`, or `BLOCKED`, with command/exit code evidence. `CURRENT` requires a real freshness check; a successful query alone is not proof. Documentation-only tasks do not need updates.
- The Codex `PreToolUse` `graphify hook-check` is a no-op in the observed 0.9.112 integration. It **does not** enforce queries or auto-refresh. Do not install Git hooks, watchers, dependencies, or change global configuration/security without explicit authorization.
- Keep existing architecture/Project map documentation as the reference for workflows, business rules, and testing; Graphify complements it with code connections.
```

**PowerShell execution detail:** This is a rule for agents, not a command that is directly run from Batch. When an agent runs a fallback, it should do something like:

```powershell
$graphifyExe = (Get-Command graphify -ErrorAction SilentlyContinue).Source
if (-not $graphifyExe) { $graphifyExe = Join-Path $env:USERPROFILE '.local\bin\graphify.exe' }
& $graphifyExe update .
```

A checked-in hook containing `C:/Users/<someone>/...` is **machine-specific** and is not portable. On a different computer, inspect `.codex/hooks.json` and correct only the Graphify command after confirming its behavior. When the hook uses bare `graphify` and Codex's process PATH omits the tool, the hook can fail even though direct CLI commands with a fallback work. The BAT reports the suspected mismatch but deliberately does **not** rewrite hooks. An otherwise successful no-op hook is still not an automatic update mechanism.

## 5. **Real** Codex CLI smoke test

Open a **new Codex session in the repo**. Do **not** select `$graphify` manually. Ask:

> Using the repository's `AGENTS.md` instructions, analyze dependencies of a real symbol in this project. Before trusting the graph, verify freshness, use a real Graphify CLI query with PATH fallback if needed, and verify the relationships in source. Report the actual CLI commands, exit codes, graph freshness evidence, and any unindexed files. Read-only; do not modify code or install anything.

A good result must show a **real query and freshness evidence**. A statement such as “Graphify was used” by itself is insufficient. If the agent says `graphify` is absent, have it try the `%USERPROFILE%\.local\bin\graphify.exe` fallback first. After a later **actual code change**, confirm the final report contains an executed incremental `update .`, not just a promise.

## 6. Git review and portability checklist

After `--setup`:

```powershell
git status --short
git diff -- AGENTS.md .codex/hooks.json
git diff --check
```

Review any generated/untracked `.codex/` files separately, plus `.graphifyignore` and `graphify-out/`. Decide whether to commit project-scoped skill/instructions or ignore machine-local artifacts according to repository policy. Do not automatically commit `graphify-out/` or a machine-specific absolute hook path. The BAT keeps pre-install backups of existing `AGENTS.md` and `.codex/hooks.json` (when present) in an explicitly printed folder under `%TEMP%`; preserve that folder until your review is complete.

### Troubleshooting summary

| Symptom | Action |
|---|---|
| `uv` missing | Approve `winget install --id astral-sh.uv --exact`, reopen terminal |
| `graphify` missing on PATH | Try `%USERPROFILE%\.local\bin\graphify.exe`; do not reinstall immediately |
| Skill not discovered | Check `.codex/skills/graphify/SKILL.md` and start a **new** Codex session |
| Graph missing | `graphify extract . --code-only` after reviewing ignore rules |
| Graph query fails on unknown symbol | Try a **real** symbol in your code; inspect Graphify output and extractor coverage |
| `HEAD` matches but code changed | Compare input hashes or use `graphify update .` |
| Hook exits 0 but no update | Expected `hook-check` no-op; actual updates are agent-driven |
| Hook fails but direct CLI works | Inspect `.codex/hooks.json` path and Codex PATH; do not assume hook dispatch is active |
| Vendored code dominates | Add specific third-party patterns to `.graphifyignore` and re-index |
| Missing HTML/JSON dependencies | Code-only indexing may omit them; verify against source |

### References

- Official project: <https://github.com/Graphify-Labs/graphify>
- Installation guide: <https://docs.graphify.com>
- `uv` installation: <https://docs.astral.sh/uv/getting-started/installation/>

**Scope:** Script supports project-local Codex integration. It does **not** assume a global Graphify skill is suitable for every repository, and does **not** modify existing `cost-efficient-coding`/delegation rules or `autoApprove` settings.
