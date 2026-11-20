# Graphify Setup for Codex & GitHub Copilot (Windows)

This package configures Graphify for **Codex CLI, Codex Desktop, and GitHub Copilot in Visual Studio**.

It supports any project language, including Python, C#, .NET/WPF, JavaScript, and TypeScript.

## 1. Run the installer

Open the `GraphifySetup` folder and run:

**`Graphify_Codex_Windows.bat`**

The installer displays:

```text
1. CHECK existing Graphify configuration
2. SETUP Graphify for a project

Choose operation [1,2]?
```

Select **2 (SETUP)** and enter the absolute path to your project:

```text
Enter absolute project path: D:\Repositories\YourProject
```

Alternatively, run the installer directly from a terminal:

```powershell
.\Graphify_Codex_Windows.bat --setup "D:\Repositories\YourProject"
```

**No additional commands are required for normal installation.**

## 2. Follow the setup prompts

The installer handles the following steps:

1. Detect the Graphify CLI and verify its version.
2. Offer to install missing prerequisites when supported, with explicit approval.
3. Install or update the bundled Graphify skill.
4. Offer each missing `## Graphify` and `## Project Map` section in `AGENTS.md` separately.
5. Offer each missing `## Graphify` and `## Project Knowledge` section in `.github/copilot-instructions.md` separately.
6. Configure `.codex/hooks.json` without overwriting unrelated configuration.
7. Copy `.graphifyignore` to the project root if it does not already exist.
8. Offer to build the initial code-only knowledge graph.
9. Run integration checks and display a final PASS/WARN/FAIL report.

Review any prompts before approving changes.

**Existing project instructions, unrelated skills, and GitHub configuration must be preserved.** Updating the Graphify skill replaces only its own folder, after confirmation and backup.

Existing instruction files are backed up before sections are appended. Existing sections are preserved; duplicate headings or missing references require manual review. Repeated setup does not append a section that is already present.

Keep `Merge-ProjectInstructions.ps1` and both instruction templates in the shared setup folder beside the BAT. The merger uses PowerShell 7 when available or Windows PowerShell under the existing execution policy.

## 3. Expected project structure

After setup, the target project should contain:

```text
YourProject/
├── AGENTS.md
├── .graphifyignore
├── .codex/
│   ├── hooks.json
│   └── skills/
│       └── graphify/
│           ├── SKILL.md
│           ├── .graphify_version
│           └── references/
│               ├── query.md
│               └── update.md
├── .github/
│   └── copilot-instructions.md
└── graphify-out/
    └── graph.json
```

`graphify-out/` is generated only if graph creation is approved and succeeds.

The installer does not generate `docs/AI/PROJECT_MAP.md`. Agents create or maintain that project-specific document from the project's source and documentation according to the Project Map instructions in `AGENTS.md`.

The reusable `GraphifySetup` package remains in its original location. It can be reused for other repositories without copying the installer into each project.

## 4. Verify the installation

The installer automatically performs integration checks after setup.

For a later verification, run the same `.bat` file and select:

**1 (CHECK)**

Or use:

```powershell
.\Graphify_Codex_Windows.bat --check "D:\Repositories\YourProject"
```

The checks cover the Graphify executable, skill, both sections in each instruction file, hooks, project graph, and a real graph query when a graph exists. Existing sections are checked for their skill or map reference; review their content when checks report missing references or duplicate headings.

Some warnings may be expected, especially if graph creation was skipped or optional components were unavailable.

## 5. Verify agent integration (optional)

Open a new Codex session in the configured project and enter:

```text
Analyze the project's main components and dependencies.

Automatically follow AGENTS.md and the Graphify skill.
Check graph freshness before analysis.
Report Graphify commands executed and freshness status.

Do not modify source code.
```

Codex should discover the Graphify instructions without manual skill selection.

For **GitHub Copilot in Visual Studio**, use Agent Mode with the necessary command execution permissions. Copilot can follow the repository instructions, but ordinary inline code completion does not guarantee Graphify execution.

## 6. Daily use

Graphify maintenance is **agent-driven**:

- **Before analysis:** Check graph freshness. Create a missing graph or update an outdated graph when writes are permitted.
- **After code changes:** Run `graphify update .` from the repository root.
- **After delegated changes:** The coordinator refreshes the graph before dependency analysis or independent QA.
- **Documentation-only changes:** No graph update is required.
- **Explicitly read-only tasks:** Do not modify graph files; use source inspection when necessary.

The skill handles executable resolution across Windows, macOS, and Linux. This installer itself is Windows-specific.

### Git and generated files

Add the following entry to the project's `.gitignore`:

```gitignore
# Graphify generated data
graphify-out/
```

Commit the Graphify configuration and agent instructions, but normally exclude generated graphs and caches.

When a repository is cloned onto another computer, the graph can be created on first use by an appropriately authorized agent.

### Important limitations

- Graphify does not continuously monitor files in the background.
- Manual edits or Git operations in Visual Studio do not immediately update the graph.
- Codex or Copilot Agent Mode can check and refresh it at the next relevant task.
- The existing `graphify hook-check` is a no-op in version 0.9.112.
- Git hooks, file watchers, software installation, and system configuration changes require explicit approval.

---

**Setup is complete when the installer reports successful integration and an agent can discover and use Graphify without manual skill selection.**
