---
name: graphify
description: Use for code dependencies, architecture, impact analysis, and graph maintenance after direct or delegated code changes.
---

# Graphify

Follow repository `AGENTS.md` and user restrictions. Skip graph queries for trivial or documentation-only tasks, but update the graph after any code change.

## CLI

Use the installed Graphify CLI on Windows, macOS, or Linux, independently of the project's language.

**Windows (PowerShell):**
```powershell
$cmd = Get-Command graphify -ErrorAction SilentlyContinue
$graphifyExe = if ($cmd) { $cmd.Source } else { Join-Path $env:USERPROFILE '.local\bin\graphify.exe' }
& $graphifyExe --version
```

**macOS/Linux:**
```bash
GRAPHIFY_BIN="$(command -v graphify || printf '%s' "$HOME/.local/bin/graphify")"
"$GRAPHIFY_BIN" --version
```

Use the resolved executable for all commands. No project-local Python or `.graphify_python` is required. Never install dependencies, modify PATH, hooks, or system settings without approval. Report CLI failures and fall back to source/JSON inspection.

## Freshness

Before trusting `graphify-out/graph.json`:

1. Compare `built_at_commit` with `git rev-parse HEAD`.
2. Inspect `git status --short` and relevant manifest hashes.
3. If commits differ, inspect `git diff --name-status <build-commit>`.
4. Matching HEAD or an untracked graph alone proves neither freshness nor staleness.
5. Update changed or uncertain graphs when writes are permitted; otherwise verify relationships in source and report uncertainty.

If the graph is missing or unusable, use source directly or run `extract . --code-only` when authorized.

**Strict read-only:** CLI queries may write `graphify-out/cache/last_query_stamp`. Use existing graph JSON and source instead when all writes are prohibited. Report cache changes separately.

## Navigation

With a current graph, prefer targeted CLI commands:

```text
query "<question>"
explain "<symbol>"
path "<A>" "<B>"
affected "<symbol>"
```

Run through the resolved executable. Avoid broad queries and excessive token budgets. Verify important relationships in source and fall back to source when graph coverage is incomplete.

For advanced usage, load only when needed:
- [Query and traversal](references/query.md)
- [Incremental updates](references/update.md)

Include Graphify guidance when delegating codebase exploration.

## Maintenance

- **First use:** If `graphify-out/graph.json` is missing (e.g., after cloning), automatically run `extract . --code-only` from the repository root before graph-dependent analysis or code changes, when writes are permitted. Respect `.graphifyignore`. If extraction fails or writes are prohibited, use source directly and report `Graphify: UNVERIFIED`. Do not install dependencies without approval.
- After any direct code change, run `update .` from the repository root before completion.
- After delegated code changes, the coordinator runs `update .` before dependency analysis or independent QA. The implementation worker need not update the graph.
- After pull, merge, or branch switch, check freshness and update when indexed code changed or the baseline is uncertain.
- Documentation-only changes require no update.
- `check-update .` checks only the non-code pending marker, not code freshness.
- Use local, code-only extraction and `.graphifyignore` to exclude vendor trees.
- Never violate explicit read-only or no-write restrictions; report when updates cannot run.

## Reporting

- `Graphify: CURRENT` — freshness verified.
- `Graphify: UPDATED` — incremental update succeeded.
- `Graphify: STALE` — confirmed outdated inputs or failed update.
- `Graphify: UNVERIFIED` — freshness cannot be established.
- `Graphify: NOT USED` — Graphify not relevant.

Report actual checks and results. Distinguish CLI execution from JSON inspection and cache writes from source/config changes.

## Hooks

Codex `PreToolUse` calls `graphify hook-check`, a no-op in Graphify 0.9.112. Maintenance is agent-driven. Installing Git hooks or additional automation requires explicit approval.
