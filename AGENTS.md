# DSMonitor project instructions

Applies to this repository: a .NET 10 Windows WPF application that observes Forge jobs and logs. DSMonitor is a monitor, not a controller of the observed agents or projects.

## Workflow and the cost-efficient coding skill

- For coding, debugging, refactoring, testing, and code review, automatically consider and use the personal `cost-efficient-coding` skill at `~/.agents/skills/cost-efficient-coding/SKILL.md`. The user need not mention it.
- This file supplies project context, correctness contracts, and navigation. It complements the skill; it does not replace its model selection, delegation, architecture/QA gates, polling, repair budgets, or usage reporting. Keep those policies in the skill instead of duplicating them here.
- Let the skill select the lowest-cost capable approach. A small local task does not require delegation; an applicable skill gate must not be bypassed because a local change looks small. Respect explicit task restrictions, including read-only work or no delegation. Do not change the user's selected model or effort silently.
- Apply project checks to the affected behavior. Do not turn the SQLite checklist below into a repository-wide audit for unrelated changes, repeat accepted reviews without new evidence, or add approval steps beyond existing authorization.

## Scope and implementation

- Start with task-relevant files and tests. Expand only to trace dependencies, investigate failures, or resolve uncertainty; verify assumptions against the implementation before editing.
- Preserve accepted behavior, architecture, and compatibility unless the authorized task changes the contract. Make the smallest complete change and fix root causes, not just the reported example.
- Reuse existing patterns, helpers, and dependencies. Preserve unrelated uncommitted work; avoid speculative refactoring and configuration changes.
- Use [docs/Plan.md](docs/Plan.md) for current scope, phase ownership, and acceptance evidence; use [Project Requirements](docs/Project%20Requirements.md) for requirement IDs and contracts. Historical status notes do not supersede later explicit acceptance decisions. Planned tables or features are not evidence of runtime implementation.
- Continue authorized work without repeated confirmation. Clarify a contradiction only when it blocks a correct decision, while progressing independent work. Do not start another phase, change acceptance status, or commit without explicit instruction.

## Architecture and database contracts

- DSMonitor is pre-production. SQLite v3 is the sole target runtime database **after the separately authorized cutover**. V3-1 is an accepted schema foundation; its acceptance does not mean the application already uses v3 at runtime. Verify current wiring before changing it.
- Do not introduce automatic v1/v2-to-v3 migration or long-term legacy compatibility solely for deployment concerns unless requested. Keep current runtime profiles until the approved cutover; preserve accepted B1/B2 algorithms when removing obsolete runtime compatibility.
- Real `events.db`, `events-v2.db`, `events-v3.db`, and their sidecars may contain useful history. Never delete, overwrite, reset, migrate, or modify actual user databases without explicit authorization. Use isolated synthetic databases for development and tests.
- Preserve accepted ingestion, physical/source identity, logical deduplication, cursor/checkpoint atomicity, coverage, exact-decimal costs, bounded retrieval, and fail-closed behavior. Schema changes need a task-specific reason, focused regressions, matching exact-manifest updates, and independent review where required by the skill or task.
- Accepted Forge logical events and `job_aggregates` are the authoritative additive Forge cost source. Derive task/project totals without counting jobs twice. OpenCode/provider usage observations corroborate costs; they are not additional additive Forge charges. Distinguish missing cost from known zero.
- Preserve explicit ownership among projects, tasks, runs, jobs, invocations, source instances, sessions, messages, results, and evidence. Common timestamps, paths, or sessions alone do not prove ownership. Keep unknown or ambiguous recovery data explicit.
- Real OpenCode history, private sessions, credentials, and live databases require explicit, narrowly scoped authorization. Use synthetic or user-approved sanitized fixtures. Do not launch the application against real data merely to validate a change.
- Keep historical/live classification, enrichment, UI integration, and the external logging outbox in their assigned phases. Table existence does not establish that those features are complete.

## Testing and regression safety

- Treat existing passing tests as regression contracts. Never delete, skip, disable, or weaken them, or alter assertions, mocks, fixtures, or snapshots merely to make incorrect behavior pass.
- Modify an existing test only for an intentional requirement change or a demonstrated test defect. Explain each change and preserve or improve meaningful coverage. Obtain approval before removing a test; reuse authorization already given for an explicit behavior change.
- Add or extend focused, behavior-based regression coverage for changed behavior where meaningful or required. Reproduce bugs before fixing them when practical. Cover valid counterparts, relevant boundaries, and failure paths; avoid redundant tests that mirror implementation details.
- For high-risk persistence work, define a proportional acceptance matrix before implementation and use it during final self-review. After tests pass, investigate additional in-scope failure modes within the authorized execution and repair limits. Self-review does not replace required independent QA.
- For **SQLite integrity changes**, use direct SQL with foreign keys enabled. Cover applicable `INSERT`, `UPDATE`, `DELETE`, `INSERT OR REPLACE`, `REPLACE INTO`, `UPDATE OR REPLACE`, and UPSERT paths, including parent mutations and harmless conflict actions. Examine nullable keys, `CHECK` expressions returning NULL, nullable uniqueness, scoped foreign keys, triggers, partial indexes, and conflicting owners where relevant.
- Verify the intended negative-test failure, not merely any SQLite exception. Check valid counterparts, transaction rollback, reopening, exact schema-manifest protection, and dependent-record preservation. Exercise recursive triggers disabled when correctness must not rely on replacement DELETE triggers firing.
- For path/identity changes, cover relevant Windows case, Unicode, and alias behavior. SQLite's ASCII-only case folding does not establish Unicode-insensitive ownership. Preserve fail-closed handling and documented residual limitations.

## Validation and environment failures

- Select checks from changed behavior and dependencies. Run relevant baselines when useful, targeted checks after coherent changes, then the broader applicable regression suite and no-restore build. Retain mandatory gates; do not repeat unchanged successful checks just to consume a routine checklist.
- Documentation-only changes need content, referenced-path/command, and final-diff checks; unrelated application tests are unnecessary. Inspect the final diff and run `git diff --check` before finishing.
- Before completion, inspect `git status --short` and review all relevant staged, unstaged, and untracked files. `git diff --check` does not validate untracked files; inspect them separately. Do not stage or commit files without authorization.
- Use existing dependencies and no-restore options. Do not install packages, restore dependencies, elevate privileges, enable Windows features, change security settings, or terminate the user's running application without explicit approval. Use an established isolated output approach if build files are locked.
- Separate standard MSTest/VSTest and TRX evidence from direct invocation, synthetic probes, and worker reports. Reuse valid evidence for the same reviewed build when authorized; historical results from before later code changes do not establish final regression safety.
- Always report the actual test count, skipped count, and applied filter. A filtered synthetic regression run must not be described as an unfiltered full-suite run. Record excluded tests separately.
- For a new environment failure, record the test, command, operation/path, error, and exit status. Confirm the diagnosis with unchanged baseline code or a minimal independent probe; access denied alone is insufficient. Reuse a confirmed diagnosis until relevant evidence changes.
- For a blocked validation command, try at most one justified alternative on the smallest failing selection within existing permissions, unless the task specifies another recovery procedure. This limit concerns validation-command retries; delegated-job polling, continuation, and repair limits remain governed by the skill and task. Do not repeatedly retry known testhost or Windows link/file-operation failures. Report unresolved checks as unverified.
- Scope searches and excerpts; batch independent reads when safe and inspect every result. Keep dependent operations sequential. Keep passing output concise and retain failure details, summaries, and exit codes.

## Repository structure and entry points

Use this map to start a focused investigation; verify current code. Update affected entries when ownership or entry points change, without adding line numbers or an inventory of every control.

### Application and monitoring flow

```text
App.xaml (StartupUri)
  -> MainWindow.xaml / MainWindow.xaml.cs (composition and shutdown)
  -> MainViewModel.StartAsync / StopAsync
  -> ForgeMonitoringService.RunAsync
  -> discovery + parsing + durable ingest
  -> MonitoringSnapshot -> MainViewModel.ApplySnapshot -> JobRowViewModel
```

| Area | Files and main responsibilities |
| --- | --- |
| Solution and projects | `DSMonitor.slnx`, `DSMonitor.csproj`, `tests/DSMonitor.Tests/DSMonitor.Tests.csproj`: app/test build configuration and dependencies. |
| Startup and composition | `App.xaml` selects `MainWindow`; `MainWindow.xaml.cs` creates the service, logger, ledger factory, and view model, and coordinates shutdown. Runtime wiring calls `EventLedger.OpenV3Projection` at `events-v3.db`; v1/v2 databases are not migrated or opened by the dashboard. |
| Dashboard | `MainWindow.xaml`: layout and bindings. `ViewModels/MainViewModel.cs`: commands, Start/Stop lifecycle, snapshots, filtering, raw-log loading, and selected-job original-request loading. `ViewModels/JobRowViewModel.cs`: per-job display, transient Live Activity and provenance, recent events, and the read-only Original Request field. `Services/JobLiveActivityReader.cs`: guarded metadata-bound 64 KB preview excerpts plus a reusable 256 KB typed, digest-bound full-report retrieval (`Complete`/`Incomplete`/`Unavailable`/`Unsafe`) for one selected text part; no content persistence or private OpenCode store access. `Services/OpenCodeOriginalRequestService.cs`: bounded read-only `opencode api get` user-message query or observed-assistant-anchored unfiltered message stream for the selected job's verified session, strict identifier/pagination validation, in-memory success-only cache, and a disabled test default; no prompt, export, credential, SQLite, or private-history access. `Converters/DashboardConverters.cs`: display converters. |
| Monitoring and discovery | `Services/ForgeMonitoringService.cs`: `RunAsync`, durable/legacy scan paths, bootstrap coordination, cancellation, and snapshots. `Services/ForgeJobDiscovery.cs`: `Discover`, job metadata/log association, and uncovered logs. |
| Parsing and reading | `Parsing/ForgeMetadataParser.cs`: `ParseFile`/`Parse`. `Parsing/ForgeEventParser.cs`: `TryParse`, safe summaries and structured source identity. `Parsing/ForgeLogTailReader.cs`: `Poll`, `PollDurable`, bounded reading, and guarded tail previews. |
| Persistence | `Persistence/EventLedger.cs`: profile opens, `Ingest`, schema/manifests, transactions, aggregate/coverage queries, and bounded retrieval. `Persistence/EventLedgerModels.cs`: contracts, options, schema versions, and `LedgerStoragePaths`. Inspect narrow regions of the large ledger file. |
| File security | `Services/PathSafety.cs`, `Services/SafeFileAccess.cs`: approved-root and guarded source reads. `Persistence/LedgerStorageGuard.cs`: database/sidecar and filesystem-identity guards. Preserve documented race limitations. |
| Timeline policy | `Services/TimelineOrdering.cs`, `Services/TimelineRetentionBuffer.cs`: shared display ordering and bounded dual-window retention. Keep service and view-model behavior consistent. |
| Monitoring output | `Services/MonitoringLogging.cs`: logger interfaces/factory and existing text/JSONL output. It does not establish a completed durable outbox. |
| Shared models | `Models/`: events, job metadata/status, options, snapshots, and monitoring state. |
| Requirements and acceptance | `docs/Project Requirements.md`, `docs/Plan.md`: contracts, roadmap, decisions, evidence, and remaining limits. Preserve unrelated edits. |

### Tests by behavior

- Persistence: `EventLedgerTests.cs`, `B2ProjectionLedgerTests.cs`, `V3LedgerProfileTests.cs`, and `EventLedgerCrashRecoveryTests.cs`.
- Monitoring/parsing/logging: `ForgeMonitoringServiceTests.cs`, `ForgeJobDiscoveryTests.cs`, `ForgeLogTailReaderTests.cs`, `ForgeEventParserTests.cs`, `ForgeMetadataParserTests.cs`, and `MonitoringRunLoggerTests.cs`.
- Timeline/UI/lifecycle: `TimelineOrderingTests.cs`, `TimelineRetentionOracleTests.cs`, `TimelineServiceViewModelTests.cs`, `JobRowTimelineOrderingTests.cs`, `MainViewModelTests.cs`, and `MainWindowSmokeTests.cs`.
- Paths and fixtures: `PathSafetyTests.cs`, `SafeFileAccessTests.cs`, `TestWorkspace.cs`, and `ReparsePointFixture.cs`. Test file names above are under `tests/DSMonitor.Tests/`.
- `EventLedgerCrashRecoveryHost.cs` is the test assembly's special child-process entry point, not the WPF application entry point. Recovery tests own and clean up only the child processes they start.
- `RealForgeDataSmokeTests.cs` reads actual `%USERPROFILE%\.forge-delegate\runs`. Its presence does not authorize real-data access; exclude it unless the user explicitly authorizes that smoke check. Report filtered coverage accurately rather than claiming an unfiltered full-suite run.

### Build and test commands

Run from the repository root with the existing development environment:

```powershell
# Build app and tests without restoring dependencies.
dotnet build .\DSMonitor.slnx --no-restore -m:1

# Synthetic regression suite; intentionally excludes the real-Forge-data smoke test.
dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName!~DSMonitor.Tests.RealForgeDataSmokeTests" --logger trx --results-directory .\TestResults

# Focused example; choose the class relevant to the task.
dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~V3LedgerProfileTests" --logger trx --results-directory .\TestResults
```

Use `--no-build` only after a successful build of the reviewed sources. TRX files use generated names to preserve earlier evidence. An explicitly authorized unfiltered run uses the same test command without `--filter`; identify the real-data access in its scope. Never report an empty selection, skipped test, aborted runner, or supplemental direct invocation as a complete standard-runner pass.

## Completion and reporting

- Report what changed and why, meaningful coverage, existing-test modifications and their justification, checks actually run with results/provenance, and material risks or unverified behavior. Keep detail proportional to the task; follow the skill's usage-reporting requirements.
- For staged implementation or acceptance tasks, distinguish implementation, pre-QA self-review, independent QA, and phase acceptance. Do not impose those gates on unrelated documentation work. Passing tests alone does not authorize an `ACCEPTED` status.
- When independent QA is required, the implementation author or the same agent context cannot serve as the independent sign-off authority. Use a separate reviewer context and distinguish author self-review from independent verification.
- Stop dependent work when a required decision or authorization is missing or the authorized repair budget is exhausted. Preserve evidence and complete unaffected authorized work; do not reset repair limits, expand scope, delete data, or start the next phase automatically.
