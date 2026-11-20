
## Graphify

- Automatically use [Graphify](.codex/skills/graphify/SKILL.md) for code dependency/impact analysis and graph maintenance after code changes, including delegated work. No manual skill selection. Skip graph queries for trivial tasks and updates for documentation-only changes.
- Follow the skill for CLI execution, freshness, read-only restrictions, updates, delegation, and reporting. The coordinator refreshes the graph after delegated code changes, before independent QA.

## Project Map

- Use [Project Map](docs/AI/PROJECT_MAP.md) as the primary overview of project architecture, modules, workflows, business rules, dependencies, and testing conventions.
- **Before relevant work:** Read the affected sections and verify important details against current source code. If the map is missing or outdated, create or update it when writes are permitted.
- **After changes:** Update affected sections when changes introduce or modify architecture, module responsibilities, dependencies, workflows, business rules, or testing conventions. Skip updates when documentation is unaffected.
- **Delegated work:** The coordinator is responsible for keeping the map current after delegated changes, before independent QA when relevant.
- Preserve existing documentation, avoid duplication, and keep updates focused. For explicitly read-only tasks, report outdated or missing documentation without modifying it.
- After coding changes, report `Project map: Updated` or `Project map: No update needed`.

