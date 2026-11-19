\# Graphify for Codex CLI: Windows Setup



This package configures Graphify for Codex CLI in any local project, on an existing or new Windows machine.



\## Step 1. Prepare the setup package



Keep the Graphify setup files in a separate folder outside your projects:



```text

GraphifySetup/

├── Graphify\_Codex\_Windows.bat

├── Graphify\_Codex\_Setup.md

├── AGENTS.md

└── .codex/

&#x20;   ├── skills/graphify/

&#x20;   └── hooks.json

```



\*\*Do not copy the setup package into each project.\*\* The setup script should configure the target repository.



Run the script from the setup folder, specifying the target project:



```powershell

.\\Graphify\_Codex\_Windows.bat --setup "D:\\Repositories\\YourProject"

```



After setup, the target project should contain:



```text

YourProject/

├── .codex/

│   ├── skills/graphify/

│   └── hooks.json

├── AGENTS.md

├── .graphifyignore       # If configured

└── graphify-out/         # Generated project graph

```



\*\*Important:\*\* Preserve existing `AGENTS.md` and `.codex/` configurations. Merge Graphify instructions without overwriting other agent rules. Ensure that `AGENTS.md` contains complete Graphify usage and maintenance instructions, not just the `## Graphify` heading.



The `.bat` script and setup documentation remain in the shared `GraphifySetup` folder for reuse across projects.



\## Step 2. Open PowerShell



Open PowerShell in the project directory.



Example:



```powershell

cd D:\\Repositories\\Python\\YourProject

```



\## Step 3. Check existing installation



```powershell

.\\Graphify\_Codex\_Windows.bat --check .

```



This checks the existing Graphify installation and project integration.



Warnings are expected on a new machine.



\## Step 4. Install or configure Graphify



```powershell

.\\Graphify\_Codex\_Windows.bat --setup .

```



Review and approve any proposed installations or configuration changes.



The setup should configure the project-local Codex integration.



\## Step 5. Verify configuration



Confirm that:



\- Graphify CLI runs successfully.

\- `.codex/skills/graphify/SKILL.md` exists.

\- `AGENTS.md` contains actual Graphify usage and update instructions, not just the `## Graphify` heading.

\- `.codex/hooks.json` references a valid executable on this machine.

\- A Graphify knowledge graph exists and can be queried.



If the graph has not been created, run:



```powershell

graphify extract . --code-only

```



Use the full executable path if `graphify` is unavailable on PATH.



\## Step 6. Verify inside Codex CLI



Open a new Codex session in the project and enter:



```text

Analyze the main components and their dependencies.

Follow the Graphify instructions in AGENTS.md.

Report the Graphify commands executed and graph freshness.

Do not modify source code.

```



You do not need to select the Graphify skill manually.



\## Daily use



Codex should:



1\. Check graph freshness before relevant codebase analysis.

2\. Use Graphify for dependencies and impact analysis.

3\. Run `graphify update .` after code changes, including delegated changes.

4\. Report if the graph is current, updated, or stale.



Graph updates depend on Codex following these instructions. The existing no-op hook does not enforce updates.



\*\*Setup is complete when a real Graphify query succeeds and Codex demonstrates that it can use the graph without manual skill selection.\*\*

