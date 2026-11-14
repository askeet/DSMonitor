# Prompt for Codex Desktop: complete local setup audit (no model delegation)

Paste this into Codex Desktop on the OLD Windows computer (prefer Sol if doing a deep audit):

```text
# Codex Desktop — Local Delegation Setup Audit

Perform this audit directly on the source Windows computer using only authorized local read access and safe non-mutating diagnostics. Do not delegate to DeepSeek or any other model/agent. If any requested files or directories are inaccessible in the current session, identify that limitation rather than claiming to have inspected them.

## Objective

Audit the currently installed Windows environment for portable backup and recovery of:

- Codex Desktop and applicable user instructions
- OpenCode and its supported CLI configuration
- Forge Delegate MCP and its actual launcher
- Configured external worker (including DeepSeek when actually configured)
- The global `cost-efficient-coding` and `delegate-work` skills
- The portable setup kit under `docs/setup/portable-kit/`

Produce a sanitized report reflecting **current verified evidence**, not assumptions from historical documentation.

## 1. Strict safety and scope

This is a **read-only local audit**, except for creation of one approved report in the destination described below.

- Do not spawn agents, invoke external AI workers, delegate this audit, or make paid API calls.
- Do not execute `Backup-Delegate`, `Restore-Delegate`, `Install-Dependencies`, or other setup scripts, including test runs, unless separately authorized.
- Do not install or update packages; change configurations, authentication, PATH, permissions, security settings, or Windows features; elevate privileges; or create Git commits.
- Do not inspect OpenCode credential SQLite, Codex `auth.json`, secret stores, chat/session history, runtime logs, or unrelated repositories.
- Never expose passwords, API keys, OAuth tokens, cookies, database passwords, or raw environment secrets. Inspect only safe fields and redact **before** displaying content.
- If a file cannot be safely read without disclosing secrets, record its presence, location and restricted status rather than opening it.
- Honor existing project and user restrictions. Do not interpret this brief as permission to broaden filesystem access.

## 2. Confirm the actual environment

Resolve locations dynamically rather than assuming names or fixed versions. When available, inspect these installed files without modifying them:

### Codex

- `%USERPROFILE%\.codex\AGENTS.md`
- `%USERPROFILE%\.codex\config.toml` (safe MCP structure only; no secret values)
- Relevant existing Codex profiles/rules, if required to explain effective configuration

### Skills

- `%USERPROFILE%\.agents\skills\cost-efficient-coding\SKILL.md`
- `%USERPROFILE%\.agents\skills\cost-efficient-coding\references\` (existing relevant references)
- `%USERPROFILE%\.agents\skills\delegate-work\SKILL.md`

Check cross-links, precedence, conditional loading, and the external delegation authorization lifecycle. Project-specific permissions in root `AGENTS.md` may be inspected only for a repository the user has authorized, and must not be changed.

### OpenCode

- `%USERPROFILE%\.config\opencode\agents\` (relevant agent configuration)
- `%USERPROFILE%\.config\opencode\opencode.json` / `opencode.jsonc`, if present
- Nonsecret runtime/version evidence from existing local commands, only when execution access is available

### Forge Delegate

- `%USERPROFILE%\.forge-delegate\config.json`
- `%USERPROFILE%\.forge-delegate\start-mcp.cmd` and any effective launcher, if present
- Supported, nonsecret MCP launch settings and references in Codex configuration

Do not assume the worker name, model, step limit, or provider from old documentation.

## 3. Inspect the portable toolkit without executing it

Use the actual repository location if the user grants access. The known relative paths are:

- `docs/setup/portable-kit/`
- `docs/setup/.agents/skills/`

Inventory any present files, including:

- `Backup-Delegate.bat` / `.ps1`
- `Restore-Delegate.bat` / `.ps1`
- `Verify-Delegate.bat` / `.ps1`
- `Install-Dependencies.bat` / `.ps1`
- `README.md` and `README_RU.md`
- `FILE-CHECKLIST.txt`
- Copied skills and their reference files

Statically inspect script logic when allowed. Compare portable copies with installed skills, including file hashes where safely accessible. Identify stale references, missing prerequisites, version differences, hardcoded paths and gaps in the backup allowlist. Never silently synchronize files.

Treat toolkit scripts as **user-managed setup scripts**, excluded from normal coding-agent modification, auditing or execution unless explicitly requested. This audit is an explicit request for **read-only inspection only**.

## 4. Findings to document

### A. Installed versions and commands

Record Node.js, npm, OpenCode, Forge Delegate and relevant package versions, executable locations and package names. Distinguish locally confirmed values from documentation-only or unknown values.

### B. MCP startup and Windows paths

Describe how Codex Desktop launches Forge Delegate: executable/wrapper type, sanitized arguments, working directory, relevant PATH handling and launcher dependencies. Identify `%USERPROFILE%`, `%APPDATA%`, npm, and drive-specific paths that may differ on a new machine.

### C. Effective worker configuration

Where verifiable, record provider, model, agent, step limit, timeout, approval configuration (`autoApprove`), background support and same-session continuation. Distinguish **configured** values from **actually tested** runtime behavior. Do not initiate a real worker call to verify them.

### D. Skill workflow

Confirm from installed files that `cost-efficient-coding` controls coding task routing, project authorization, architecture gates, worker ownership, repairs, independent QA, safety and reporting; `delegate-work` supplies Forge execution mechanics. Check that specialized MSTest/Windows and desktop UI references are loaded only for applicable tasks. Document root `AGENTS.md` authorization bootstrap and reuse, including that recorded project permissions cannot override platform gates. Do not claim automatic natural-language skill activation has been tested unless actual evidence exists.

### E. Backup coverage and exclusions

List required/optional files in the actual backup scripts and identify missing coverage or risky inclusions. Exclude credentials, tokens, private runtime databases, session histories, caches, logs, unrelated repositories and unnecessary personal data. Warn when ordinary configuration files may themselves contain secrets. Explain backup integrity, ZIP manifest/hash verification, restore conflicts and replacement safeguards based on observed script logic, not assumptions.

### F. Migration checklist

Provide a concise source-to-target Windows checklist covering reinstallation prerequisites, user-profile relocation, npm/Node paths, launcher resolution, global skills and relative links, project `AGENTS.md` handling, reauthentication, configuration comparison, offline verification and rollback. Do not assume identical drive letters, usernames or tooling versions.

### G. Verification and risks

Offer **offline, non-mutating** verification commands separately from an **optional, explicitly approved paid smoke test**. Do not run the latter. Record any blockers, conflicting values, untested assumptions or security concerns as `Verified`, `Documented only`, `Unverified` or `Blocked`.

## 5. Report location and contents

Create exactly one output document named `DELEGATION_LOCAL_AUDIT.md` in the **user-approved backup working directory**, alongside the encrypted backup ZIP if that directory is available and writable. Do not assume that the application repository or `docs/setup/portable-kit/` is approved for storing machine-specific audit data. If no approved writable destination exists, prepare the report in the current conversation or approved artifact workspace and state the limitation; do not change filesystem permissions.

The report should contain:

1. Executive summary and evidence coverage
2. Verified versions and installed paths
3. MCP launch and worker configuration
4. Global skills, references and authorization workflow
5. Portable-kit inventory and installed-versus-portable comparison
6. Backup coverage, exclusions, integrity and restore behavior
7. Migration checklist
8. Safe offline verification commands
9. Optional paid smoke-test instructions (not executed)
10. Risks, unknowns and blocked checks

Cite or identify safe evidence (sanitized paths, configuration keys, command names, file hashes where available). Do not dump full configuration files.

## 6. Final checks

Before reporting completion, verify and disclose:

- What was actually inspected, and what was inaccessible
- Whether any potentially sensitive values were excluded or redacted
- Whether no authentication stores were opened and no secret values were exposed
- Whether no external worker/API calls, setup scripts, installs, system changes or commits occurred
- Whether the report is appropriate for encrypted backup storage versus **public sharing** (assess separately)

The resulting `DELEGATION_LOCAL_AUDIT.md` supplements `README.md` and `README_RU.md`; it does not replace them. Do not automatically edit those READMEs.
```

Save the resulting `DELEGATION_LOCAL_AUDIT.md` next to the encrypted ZIP. It is a supplement to `README_RU.md`, not a replacement for the actual files.
