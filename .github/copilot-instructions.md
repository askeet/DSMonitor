
## Graphify

For code analysis and changes, follow the Graphify rules in `AGENTS.md` and `.codex/skills/graphify/SKILL.md`.

Check graph freshness before analysis; create or update the graph when needed. Update after every code change, including delegated work (coordinator responsibility).

For explicitly read-only tasks, skip writes and verify against source. Do not install dependencies or modify configuration without approval.

## Project Knowledge

Follow the Project Map maintenance rules in `AGENTS.md`. Read the relevant sections of
`docs/AI/PROJECT_MAP.md` and verify important details against source code.
When writes are permitted, create a missing map from the project's source and documentation, or
update affected sections when architecture, responsibilities, workflows, business rules,
dependencies, or testing conventions change. For read-only tasks, report any gaps.
