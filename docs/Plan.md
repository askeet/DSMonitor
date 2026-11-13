# DSMonitor — Executable Development Roadmap

**Document version:** 2.0  
**Date:** 2026-11-09
**Source of truth:** `docs/Project Requirements.md` (stable requirement IDs) and this roadmap.  
**Current phase:** Phase 1 **IN PROGRESS / NOT ACCEPTED**. B1 is **ACCEPTED** and B2 is **ACCEPTED with documented limitations**: the durable ledger supports historical bootstrap, live monitoring and job-owned aggregates. Supported source-ID replay is durably deduplicated; arbitrary logical exactly-once behavior and producer ID stability are not guaranteed. B3 owns durable external-log delivery. See sections 1.10–1.13 for evidence and remaining gates.
**Milestones:** B1 and B2 **ACCEPTED**; B3 and B4 **NOT STARTED**, in that order. B4 is an internal Phase 1 gate, distinct from application Phase 4.
**Future phases:** Phase 2–4 are **NOT STARTED**.

> Status vocabulary: `NOT STARTED / IN PROGRESS / COMPLETED / BLOCKED`.
> A task is checked only when implementation and the required validation actually succeed.

---

## V3 delivery order — Live Activity first

### V1 job-table delivery decisions

The user has explicitly decided the following sequence; this does not change Phase 1–4 or B1–B4 acceptance:

| Stage | Status | Decision |
|---|---|---|
| A1 — Full Report Retrieval | **ACCEPTED** | Preserve the accepted guarded 256 KiB retrieval and its regression contracts. |
| A2 — Reported Status Extraction | **DEFERRED FOR V1 — Known limitations accepted by user** | The parser is not technically accepted or fully verified. Unresolved defects are tracked in the Beta Stabilization backlog below. |
| A3 — Job Table Data Preparation | **NEXT / UNBLOCKED** | A2 correctness does not block this stage. This roadmap update does not authorize starting implementation. |

The previously planned five-column table structure and remaining V1 UI stages are unchanged. Reported Status is unverified agent-reported text, never authoritative Forge execution state: do not use it for completion logic, counters, notifications, or workflow decisions, and do not present `COMPLETED` as verified success. Preserve job/source isolation, provenance, and full values internally where available. Until reliable presentation is established and separately approved, the Reported Status column may use a neutral `N/A` placeholder rather than risk false attribution. Beta Stabilization must review every deferred issue and its acceptance evidence before deciding beta release readiness.

The V3 delivery order below describes priority, not authorization to implement or change acceptance. Existing Phase 1–4 and B1–B4 acceptance statuses and gates remain unchanged. The user has authorized early read-only OpenCode observation strictly scoped to known Forge jobs through verified session/message identities; this P0 slice may precede Phase 1 acceptance and the broader Phase 2 integration. It must not access unrelated conversations or initiate execution. Verify the access mechanism and attribution before use; this roadmap does not authorize implementation during this documentation task. Comprehensive history remains deferred. B3 and B4 remain NOT STARTED with their existing dependencies.

**Product priorities (package statuses are historical except for the V1 decisions above):**

- **P0 — Job-scoped OpenCode Live Activity:** deliver V3-2–V3-4 using the accepted SQLite v3 foundation, with the separately approved runtime cutover and the early dashboard gate below. The current runtime is v2; acceptance of V3-1 did not switch it.
- **P1 — DeepSeek API access:** design secure user-provided credential handling and read-only access only to verified supported API capabilities. Verify capabilities and authorization before implementation; do not assume historical request or execution logs exist. No model execution or prompts. Record unavailable capabilities honestly.
- **P2 — ChatGPT Work correlation research:** investigate supported, authorized access to the active conversation and reliable correlation with Forge delegation identities. This is research, not a promise of access or permission to inspect unrelated conversations; unverified or ambiguous associations remain unknown.

Comprehensive historical reconstruction is not a prerequisite for P0, P1 or P2. These priorities are separate from the existing V3 package numbering and do not change B3/B4 or Phase acceptance statuses. Future implementation, credential-storage design, v3 cutover and any access beyond the authorized job-scoped OpenCode boundary remain subject to their applicable authorizations.
The documents record V3-1 and mention V3-2 as not started, but do not define V3-2 through V3-7 packages. No old package scopes are evidenced to remap. The following assigns new IDs and maps existing work by scope, without inventing prior package definitions. All new packages and the early milestone are **NOT STARTED**; accepted B2 capabilities do not establish their completion.

| New package / old reference | Priority and dependencies |
|---|---|
| V3-1 / existing V3-1 | **ACCEPTED** SQLite foundation; section 1.14 evidence unchanged. |
| V3-2 / previously only named NOT STARTED | Live Monitoring Foundation: reuse accepted B1/B2 ingestion, identity, deduplication, atomic cursor/checkpoints, coverage and cost behavior; durably capture current jobs and activity, including verified job-scoped OpenCode messages, tool activity and results, with minimum historical/live separation and restart correctness. Requires V3-1 and a separately approved, implemented v3 runtime cutover before v3-dependent runtime acceptance. Current runtime remains v2. |
| V3-3 / no documented old package | Projects, Chats & Tasks: durable explicit associations, distinguishable project identities, display names/overrides and N/A handling. Depends on V3-2; use verified job-scoped OpenCode evidence, including original requests and available session titles, as early as technically feasible. |
| V3-4 / no documented old package | Early Live Activity UI: gate below, after V3-2 and minimum V3-3 associations and display handling. Remaining V3-3 enhancements can follow; unavailable fields are N/A. |
| V3-5 / no documented old package | Enhanced Content, Results & Evidence: richer permitted summaries, outcomes and QA evidence; follows early UI and approved sources. |
| V3-6 / no documented old package | Costs and Analytics: extend reporting after live identity/associations; use accepted Forge logical events and job aggregates once per job; provider/OpenCode usage corroborates, never adds another Forge charge; distinguish missing cost from zero. |
| V3-7 / no documented old package | Historical Recovery: later reconstruct older activity not yet stored from specifically approved Forge/OpenCode sources; preserve unknown and ambiguous records, provenance and existing history. Depends on live identity and separation; never a prerequisite for V3-4. |
| Integrated acceptance / no documented old V3 package | After capabilities included in the authorized release are completed, validate them together, including dependencies and remaining limits. Distinct from B4; does not itself accept Phase 1. |

Cutover must be separately approved and planned against accepted B1/B2 guarantees. SQLite v3 is the sole intended runtime database after cutover; V3-1 did not perform it. No automatic v1/v2 migration or long-term compatibility is required. Preserve real databases, sidecars and recorded history unless explicitly authorized otherwise. Existing B2 bootstrap and coverage behavior remains a regression contract: prioritize newly observed activity fairly and classify initial records without treating old events as new; do not remove accepted correctness to avoid comprehensive recovery.

### Early Live Activity Dashboard acceptance gate — NOT STARTED

- Show observed Forge jobs in WPF with Project, Chat, Task, Doing, Done, Status, Last Updated and stable Job identity with relevant session association; populate labels and summaries from safely attributed Forge and job-scoped OpenCode evidence as early as technically feasible; use `N/A` only when genuinely unavailable or unsafe to attribute.
- Update running jobs as activity is observed within documented refresh and bounded-read limitations; expose source and freshness of status. Retain the last verified or reported summary and status for completed jobs across restart, with provenance and no fabricated outcomes.
- Preserve monitoring, logging, ingestion and cost behavior; restart must not relabel historical events as new live activity. Do not guess job/session/task/project associations from timestamps or matching directory names.
- Distinguish execution completion, independent QA and explicit user acceptance. Accept this milestone without reconstructing all historical OpenCode sessions; comprehensive enrichment is V3-5 and V3-7 work.

Source gaps and field-level contracts are specified in [Project Requirements](Project%20Requirements.md#34a-p0-live-activity-dashboard). Forge metadata alone does not establish all fields. The user reports prior verified research showing job-specific assistant messages, tool activity, results, and an example mapping original requests to four Forge jobs sharing one session. Preserve that evidence without treating a shared session as proof of ownership; verify the actual source mechanism and message-level associations when implementing. Available session titles and safely attributed requests and summaries are early P0 inputs, not V3-7 history work.

---

## Phase 1 — WPF Dashboard and Forge Monitoring

**Status: IN PROGRESS / NOT ACCEPTED** — B1 and B2 are accepted within their documented limitations (sections 1.10 and 1.11). B2 integrates durable aggregates, historical bootstrap and live monitoring; independent B2 QA approved the final implementation. B3 and B4 remain not started, and final Phase 1 acceptance remains pending.

**Goal:** Deliver a polished, read-only WPF dashboard that starts and stops DSMonitor monitoring, discovers existing/new forge-delegate jobs, incrementally reads metadata and logs, shows real events/statuses safely, and writes one unique monitoring log per Start.

**Dependencies:** existing DSMonitor .NET 10 WPF solution; forge-delegate 1.2.4 runs directory; .NET 10 SDK and MSTest package restore; no OpenCode REST dependency.

### 1.1 Tasks

- [x] Review `docs/Project Requirements.md`, `docs/Plan.md`, `docs/FormView.png`, and the existing WPF project.
- [x] Inspect installed forge-delegate 1.2.4 metadata and JSONL formats before designing parsers.
- [x] Preserve the original source/docs baseline outside the live solution and record a baseline build.
- [x] Implement MVVM dashboard shell with dark theme and reusable styles. `[UI-001..020, ARC-001, ARC-006]`
- [x] Implement STOPPED startup and Start/Stop lifecycle. `[FR-001..005, FR-030..035, LIFE-014]`
- [x] Implement forge runs discovery for existing and newly created jobs. `[FR-010..019, DATA-001..008]`
- [x] Complete defensive path validation coverage: guarded opens and junction tests independently verified; real file-symlink integration test passed separately in the user's normal Windows session. `[DATA-002..008, SEC-012]`
- [x] Resolve large-batch event loss: independent reproduction verified all 800 events persisted to both logs with UI capped at 500. `[DATA-010..020, PERF-001..005]`
- [x] Complete timeline retention: bounded dual-window retention and independent full-history oracle coverage implemented; subsequent independent B2 QA approved the integrated implementation (section 1.11). `[UI-015, DATA-015, PERF-004, PERF-007]`
- [x] Implement event dedupe and bounded timelines. `[DATA-015, DATA-017, TEST-006]`
- [x] Apply retained/recent event acceptance before cost, session, counter, and aggregate updates; deterministic cost-bearing replay regressions added. `[DATA-015, LOG-005, TEST-006]`
- [x] Implement explicit forge status mapping, UNKNOWN default, and separate monitoring state. `[LIFE-001..013]`
- [x] Implement job/session correlation without merging jobs. `[LIFE-009..013, FR-020]`
- [x] Implement IDLE with visible UNKNOWN count when no active job is confirmed. `[FR-013, LIFE-007..008]`
- [x] Implement per-run unique `.log` and `.jsonl` logging with safe summaries and graceful failures. `[LOG-001..011, SEC-011]`
- [x] Implement job table, details tabs, real timeline, bounded raw-log preview, status bar, and empty/error states. `[UI-010..020, FR-040..046]`
- [x] Complete security validation: guarded metadata/tail/preview opens independently reviewed with junction coverage passing; real file-symlink behavior subsequently verified by the user. `[SEC-001..015]`
- [x] Add meaningful automated tests for parsing, tailing, lifecycle, status, logs, cancellation, paths, and UI projection. `[TEST-001..017, TEST-019..020]`
- [x] Run focused tests, full relevant test suite, and solution build. `[ACC-P1-012]`
- [x] Implement and accept the B1 durable SQLite event-ledger foundation within the limits and evidence in section 1.10; subsequent live integration is covered by B2. `[LEDGER-001..020]`
- [x] B2 durable aggregates, historical bootstrap and live pipeline integration accepted with documented limitations (section 1.11).
- [ ] B3 durable logging outbox (section 1.12), after B2 acceptance.
- [ ] B4 final Phase 1 acceptance (section 1.13), after B2 and B3 acceptance.
- [ ] Independent Sol QA acceptance of Phase 1 behavior: review performed, CHANGES REQUIRED (see findings below). `[ACC-P1-013]`
- [x] Independent visual check: running dashboard STOPPED → Start → eight real UNKNOWN jobs / IDLE → Stop → STOPPED → clean exit. `[ACC-P1-014]`
- [ ] Record Phase 1 acceptance sign-off. `[ACC-P1-015]`

### 1.2 Phase 1 requirement coverage

| Area | Requirement IDs |
|---|---|
| Dashboard and visual shell | `PRJ-001..005`, `UI-001..020`, `FR-040..046` |
| Lifecycle and monitoring state | `FR-001..005`, `FR-010..019`, `FR-030..035`, `LIFE-001..014` |
| Forge metadata and logs | `DATA-001..020`, `ARC-003..005`, `PERF-001..010` |
| Session correlation | `FR-020`, `LIFE-009..013` |
| Logging | `LOG-001..011` |
| Safety and errors | `SEC-001..015`, `ERR-001..011`, `ERR-014..021` |
| Testing | `TEST-001..017`, `TEST-019..021` |
| Durable ledger foundation (B1) | `LEDGER-001..020` |
| Acceptance | `ACC-P1-001..015` |

### 1.3 Acceptance criteria

- [x] Existing solution builds on .NET 10 with zero warnings/errors.
- [x] MainWindow loads, lays out, and renders empty/populated states without XAML exceptions.
- [x] Start Monitoring creates a unique run log and begins forge observation.
- [x] No confirmed active job results in IDLE with UNKNOWN count visible.
- [x] Existing jobs are displayed; missing status is UNKNOWN, not guessed.
- [x] New jobs are detected without restarting DSMonitor.
- [x] Selecting a job displays actual available metadata/events.
- [x] Incremental reads preserve all accepted events: independent reproduction persisted 800/800 in both JSONL and text logs with bounded UI retention. The late-missing 502-event source matches an independent full-history oracle under the recorded partitions. The later accepted B2 integration durably deduplicates supported source-ID replays after bounded-memory eviction; crash-safe external-log delivery remains B3 work.
- [x] Stop Monitoring stops only DSMonitor and closes its log files.
- [x] Repeated Start/Stop cycles create unique logs and clean up watchers/writers.
- [x] Forge files are byte-for-byte unchanged by monitoring.
- [x] No HTTP/OpenCode REST/Prompt/permission/process-control code exists in Phase 1.
- [x] All 65 original tests and all prior applicable tests pass; the prior full suite was 101 passed / 1 skipped / 102 total. Its only skip was the environment-conditional real file-symlink fixture, which subsequently passed in a separate targeted run.
- [ ] Independent Sol QA accepted. **CHANGES REQUIRED**
- [x] B1 ledger foundation and B2 live integration accepted within the limits and evidence in sections 1.10 and 1.11.
- [ ] B2 and B3 accepted and B4 completed; checked items above record historical evidence, not acceptance of the integrated pipeline.
- [x] Independent visual check of running dashboard passed; broader DPI/accessibility validation remains Phase 4.

### 1.4 Validation evidence

| Check | Command | Result |
|---|---|---|
| Baseline build — commit `9e4ac10` | `dotnet build .\DSMonitor.slnx` | Passed before this task: 0 warnings, 0 errors |
| Baseline full suite | `dotnet test .\Tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build` | Passed before this task: 82 passed, 1 skipped, 83 total; all 65 original tests preserved |
| Baseline immediately before latest retention correction | `dotnet build .\DSMonitor.slnx --no-restore`; `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore` | Build 0 warnings / 0 errors; 102 passed / 0 skipped / 102 total in this environment (Developer Mode now permits the real file-symlink test) |
| Independent full-history oracle suite | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~TimelineRetentionOracleTests"` | 6/6 passed: late-missing 502 source across 7 partitions with intermediate prefixes; >10,000-key high-timestamp replay regression; 32 actual service→VM single-batch count/shape jobs; 229 valid scenario/partition buffer-oracle executions with visible order checked at every prefix; exact 2×500 retained-event bound; 502 one-line reader polls with repeated-poll and duplicate dedupe |
| Targeted timeline + oracle suite | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~TimelineOrderingTests|FullyQualifiedName~TimelineServiceViewModelTests|FullyQualifiedName~JobRowTimelineOrderingTests|FullyQualifiedName~TimelineRetentionOracleTests"` | 33/33 passed |
| Integration/security regression | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~PathSafetyTests|FullyQualifiedName~SafeFileAccessTests|FullyQualifiedName~ForgeLogTailReaderTests|FullyQualifiedName~ForgeMonitoringServiceTests"` | 37/37 passed |
| Full local suite after latest retention correction | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore` | 109 passed / 0 skipped / 109 total; all 102 pre-dual-window tests and all 65 original tests preserved |
| Baseline immediately before cost-replay remediation — 2026-11-09 | `dotnet build .\DSMonitor.slnx --no-restore`; `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore` | Build 0 warnings / 0 errors, exit 0; 109 passed / 0 skipped / 109 total, exit 0 |
| Pre-fix cost-replay regression | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~CostBearing"` | Failed as expected: 1 passed / 2 failed / 3 total, exit 1. The definitive failure doubled cost from `0.75` to `1.5` after a replay whose recent key had expired but whose event remained retained. The other failure was a test-only attempt to read the live logging file; the assertion was moved after Stop before production changes. |
| Post-fix cost-replay and reader acceptance gate | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~CostBearing|FullyQualifiedName~LogReplacement_DoesNotDoubleCountCost|FullyQualifiedName~Poll_RejectedCostEvent"` | 5 passed / 0 failed / 5 total, exit 0 |
| Timeline/oracle + cost-replay targeted | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~TimelineOrderingTests|FullyQualifiedName~TimelineServiceViewModelTests|FullyQualifiedName~JobRowTimelineOrderingTests|FullyQualifiedName~TimelineRetentionOracleTests|FullyQualifiedName~CostBearing"` | 36 passed / 0 failed / 36 total, exit 0 |
| Integration/security/logging regression | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~PathSafetyTests|FullyQualifiedName~SafeFileAccessTests|FullyQualifiedName~ForgeLogTailReaderTests|FullyQualifiedName~ForgeMonitoringServiceTests|FullyQualifiedName~MonitoringRunLoggerTests"` | 47 passed / 0 failed / 47 total, exit 0 |
| Full local suite after cost-replay remediation | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore` | 114 passed / 0 skipped / 114 total, exit 0; all 109 pre-remediation tests and all 65 original tests preserved |
| Final solution build after cost-replay remediation | `dotnet build .\DSMonitor.slnx --no-restore` | 0 warnings / 0 errors, exit 0 |
| Baseline immediately before B1 — 2026-11-09 | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore` | 114 passed / 0 skipped / 114 total, exit 0; baseline solution build 0 warnings / 0 errors after the concurrent-build lock was cleared |
| B1 focused ledger suite | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~EventLedgerTests"` | 31 passed / 0 failed / 31 total, exit 0 |
| B1 timeline partition-invariance regression | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~TimelineOrderingTests|FullyQualifiedName~TimelineServiceViewModelTests|FullyQualifiedName~JobRowTimelineOrderingTests|FullyQualifiedName~TimelineRetentionOracleTests"` | 33 passed / 0 failed / 33 total, exit 0 |
| B1 logging/integration/security regression | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~PathSafetyTests|FullyQualifiedName~SafeFileAccessTests|FullyQualifiedName~ForgeLogTailReaderTests|FullyQualifiedName~ForgeMonitoringServiceTests|FullyQualifiedName~MonitoringRunLoggerTests"` | 47 passed / 0 failed / 47 total, exit 0 |
| B1 full local suite | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build --no-restore` | 145 passed / 0 skipped / 145 total, exit 0; all 114 pre-B1 tests and all 65 original tests preserved |
| B1 solution build | `dotnet build .\DSMonitor.slnx --no-restore` | 0 warnings / 0 errors, exit 0 |
| Diff hygiene | `git diff --check` | Clean (no whitespace errors), exit 0; only Git's LF→CRLF working-copy notices were emitted |
| Timeline policy tests | `dotnet test .\Tests\DSMonitor.Tests\DSMonitor.Tests.csproj --filter "FullyQualifiedName~TimelineOrderingTests|FullyQualifiedName~TimelineServiceViewModelTests|FullyQualifiedName~JobRowTimelineOrderingTests"` | 27/27 passed |
| 502-event ambiguity/partition regression | `ServiceToViewModel_AmbiguousModePersistsAcrossPartitionsAfterBarrierEviction` | Passed; single/501+1/1+501/250+252/500+2 all retain identical 500..1 window; 502/502 accepted events in each `.log` and `.jsonl` |
| Symlink fixture, earlier run | `dotnet test .\Tests\DSMonitor.Tests\DSMonitor.Tests.csproj --filter "FullyQualifiedName~OpenRead_FileSymbolicLink"` | Skipped; Win32 1314 (privilege required) with manual gate message |
| Real file-symlink integration, manual | `OpenRead_FileSymbolicLink_FailsClosedWhenSupported`, run with `--no-build --no-restore` | VERIFIED / PASSED: user's normal non-administrator Windows PowerShell session, outside Codex sandbox; 1 total, 1 passed, 0 failed, 0 skipped; test 27 ms, run 0.4079 s. User-reported output. |
| Phase 1 solution build | `dotnet build .\DSMonitor.slnx` | Passed: 0 warnings, 0 errors |
| Full local suite | `dotnet test .\Tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build` | Passed: 102 total, 101 passed, 1 skipped (environment-conditional symlink test); all 65 original and all prior passing tests preserved |
| UI empty-state smoke | MSTest `MainWindow_LoadsLaysOutAndRendersWithoutException` | Passed in full suite |
| UI populated-state smoke | MSTest `MainWindow_PopulatedStateRendersAndShowsRealDataControls` | Passed in full suite |
| Read-only check | `Run_ReadOnly_DoesNotModifyForgeFiles` | Passed |
| Unique logs / close check | `Run_RepeatedLifecycle_CreatesUniqueLogsAndClosesThem` | Passed |
| Large-batch logging | `Run_LargeBatch_LogsAllEventsWhileSnapshotRetainsNewest500` | Passed: 800 JSONL events, 500 UI snapshot events |
| Path confinement | `OpenRead_JunctionEscape_IsRejectedBeforeContentRead`, `Discovery_RootJunction_FailsClosed`, `Reader_PathReplacedWithJunctionAfterConstruction_FailsClosed` | Passed |
| Timeline ordering | `TimelineOrderingTests`, `JobRowTimelineOrderingTests`, and `TimelineServiceViewModelTests` | 24/24 passed; service→VM coverage includes 500/501/800, out-of-order batches, missing barriers, all-missing, mixed retention, deterministic repeats |

### 1.5 Known limitations / risks

- forge-delegate 1.2.4 metadata has **no status field**. Therefore current real jobs display UNKNOWN by default; RUNNING/TIMED OUT require explicit future metadata/events. This is intentional and safer than PID inference.
- A past forge deadline is shown as wrapper-deadline information, not as worker termination. Historical completed jobs are not relabeled TIMED OUT without explicit evidence.
- Cost is available only when `step_finish.part.cost` appears in logs; otherwise it shows `—`.
- Tool Activity/Tests/Files tabs are Phase 2 placeholders; Phase 1 only has event summaries and a bounded Raw Log preview.
- Saved monitoring history browsing/replay is Phase 3. Phase 1 creates the files but does not provide a history UI.
- Settings is a disabled Phase 4 placeholder.
- Earlier independent Sol QA returned CHANGES REQUIRED for timeline and replay defects; the later B2 implementation and independent B2 approval supersede that milestone-level finding (section 1.11). Final Phase 1 QA remains pending. The real file-symlink test passed separately.
- B2 durably deduplicates supported job-scoped source IDs even after memory eviction. Physical-record identity is separate from logical-event identity: missing, regenerated or reused producer IDs and undetected source rewrites limit the guarantee. Arbitrary semantic exactly-once processing is not established; B3 owns durable external-log delivery.
- FileSystemWatcher is best-effort; periodic reconciliation is the correctness backstop.
- Earlier sandbox attempts to create file symlinks returned Win32 1314 even with `SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE`. A later Codex sandbox test run hung without a result and was stopped. After the user enabled Developer Mode, the user confirmed a real symbolic link using `mklink` and passed the integration test outside the sandbox without elevation. Windows PowerShell 5.1 `New-Item` had previously reported an administrator privilege requirement.

#### Deferred Reported Status V1 beta stabilization

Reported Status is a bounded top-level parser, not a Markdown engine. The examples in RS-BETA-001–005 were verified as conservative unavailable or ambiguous outcomes; that does **not** establish that all complex Markdown fails safely. Independent QA reproduced the false-attribution and Unicode defects in RS-BETA-006–011. These remain unresolved and require a neutral unavailable presentation for V1 when reliable attribution cannot be established. The user accepts deferring their correction, not their correctness. Job isolation, guarded retrieval, source identity, and provenance remain required and are not deferred.

The specific prior regressions for trailing-text fence closure, indented-code declarations, the `BuildStatus` substring example, emphasized second-label conflicts, invalid backtick-opener info, and NBSP/EMSP structural whitespace were subsequently observed to pass; their broader categories are not thereby proven safe. QA also found the generated corpus covers fewer combinations than its counts suggest (RS-BETA-012).

Evidence for RS-BETA-001–005: independent synthetic QA verified the literal examples against the reviewed parser. Their severity is P3 for the documented conservative suppression or ambiguity, distinct from the P2 false-attribution cases below. The exact supplementary-letter scalar used in RS-BETA-010 and a complete synthetic input for RS-BETA-011 were not recorded in the available QA report; retain those findings and verify the precise reproductions during Beta Stabilization.

| ID | Reproduction | Current V1 behavior | Expected future | Risk / user impact | Reason | Milestone | Future acceptance test |
|---|---|---|---|---|---|---|---|
| `RS-BETA-001` | `> quote`, then `# Heading`, then `Status: DONE` without a blank line | `Unavailable`/`N/A`: non-blank lines after `>` remain lazy continuation; a heading or list that should end the quote is not recognized | Recognize the block-quote end and parse the later top-level `Status: DONE` | A valid top-level status can be suppressed as N/A; other quote cases can falsely attribute content (RS-BETA-006–009) | Continued block-quote recognition needs more context handling; V1 intentionally treats this example as N/A | Beta Stabilization | `ReportedStatus_Beta_BlockQuoteEndsBeforeHeading` |
| `RS-BETA-002` | `- item`, then an indented fenced block containing `Status: FAILED`, then a blank line and top-level `Status: DONE` | `Unavailable`/`N/A`: a fence inside list context marks the report uncertain | Track fenced code relative to list nesting, ignore the example, and return the top-level `DONE` | Valid reports can be N/A; false attribution occurs in other contexts (RS-BETA-006–009) | List-relative fence ownership is not implemented without a general Markdown engine; fail closed | V3-2 beta stabilization | `ReportedStatus_Beta_FencedCodeInsideListDoesNotHideTopLevelStatus` |
| `RS-BETA-003` | `- item`, then nested `- Status: DONE`; also blockquote-owned declarations | `Unavailable`/`N/A`: status-like content in list or quote context marks the report uncertain | Classify the declaration as non-top-level and ignore/report context without making the whole report N/A | N/A for reports containing non-top-level status-like text; other contexts can be falsely attributed (RS-BETA-006–009) | List/quote ownership and continued-list detection are deferred; V1 does not guess | V3-2 beta stabilization | `ReportedStatus_Beta_NestedListStatusIsNotTopLevel` |
| `RS-BETA-004` | `Status: "Status: DONE"` or ``Status: `Status: DONE` `` | `Ambiguous`, no selected value | Mask quoted and inline-code spans during conflict detection and preserve the outer complete value | Conservative ambiguity/N/A for these examples; other example contexts can be falsely attributed (RS-BETA-006–009) | Quote and inline-code masking is not implemented; ambiguity is safer than guessing | V3-2 beta stabilization | `ReportedStatus_Beta_QuotedOrInlineExampleDoesNotConflict` |
| `RS-BETA-005` | `Status: **Build Status : passing**` | `Ambiguous`, no selected value | Define whether an exact `Status` token after a word plus space is a conflict or part of the value, then preserve the accepted value | Conservative ambiguity/N/A for this example; other contexts can be falsely attributed (RS-BETA-006–009) | Exact-label boundary versus multi-word embedded text is unspecified; ambiguity is safer than guessing | V3-2 beta stabilization | `ReportedStatus_Beta_MultiwordEmbeddedLabel` |
| `RS-BETA-006` | ``Example: ` `` followed by `Status: FAILED` on the next line and a closing backtick on the third | `Reported/FAILED` from a multiline inline-code span (independent synthetic QA) | Do not treat code-span content as a report declaration; return a safe unavailable result when uncertain | P2: an example can be shown as the job's reported status | Deferred by user for V1; safe presentation is required | Beta Stabilization | Synthetic multiline inline-code examples never yield a status; valid top-level counterparts remain readable |
| `RS-BETA-007` | `<pre>`, `Status: FAILED`, `</pre>` on separate lines | `Reported/FAILED` from the HTML preformatted block (independent synthetic QA) | Ignore or safely reject preformatted example content | P2: an example can be shown as the reported status | Deferred by user for V1; safe presentation is required | Beta Stabilization | Preformatted blocks cannot supply a status; valid outside declarations are handled safely |
| `RS-BETA-008` | `<!--`, `Status: FAILED`, `-->` on separate lines | `Reported/FAILED` from an HTML comment (independent synthetic QA) | Ignore or safely reject commented content | P2: hidden example text can be shown as a status | Deferred by user for V1; safe presentation is required | Beta Stabilization | Commented declarations cannot supply a status; outside declarations are handled safely |
| `RS-BETA-009` | `> Example response:`, then a fenced block containing a blank line and `Status: FAILED`, then outside `Status: DONE` | Result is `Unavailable`, but its typed declaration and provenance point to fenced `FAILED` (independent synthetic QA) | Do not expose code-example content as declaration evidence; return safe evidence or unavailable without it | P2: consumers may misread incorrect evidence even when no value is selected | Deferred by user for V1; unreliable evidence must not be presented as verified | Beta Stabilization | Synthetic quote/fence nesting never publishes fenced text as a declaration or its provenance |
| `RS-BETA-010` | `Status: **Cafe\u0301Status : passing**`; also a supplementary Unicode letter immediately before `Status` | `Ambiguous`; the composed `CaféStatus` counterpart preserves the value (independent synthetic QA) | Match label boundaries by Unicode scalar and combining sequence so the complete value is preserved | P2: a valid status is unavailable depending on Unicode representation | Deferred Unicode boundary handling for V1 | Beta Stabilization | Decomposed, composed, and supplementary-letter counterparts preserve the same full value without a false conflict |
| `RS-BETA-011` | A status value containing supplementary Unicode format character U+E0001 | Character-safety check accepts the surrogate pair without checking its category (independent synthetic QA) | Reject or safely classify the format character without silently presenting unsafe text | P2: invisible formatting can mislead status presentation | Deferred scalar-aware character-safety handling for V1 | Beta Stabilization | Supplementary format characters fail safely; valid supplementary letters remain accepted |
| `RS-BETA-012` | Deterministic corpus seed `20261111` selects 9 of 36 wrapper/context pairs; seed `20261112` reaches 4 of 8 scenarios and only agreeing conflict pairs | Counts are accurate but generated coverage is narrower than intended (independent QA of `ReportedStatusContractCorpusTests.cs`) | Generate and verify the intended pairwise/scenario coverage with reproducible seeds | P3: missing combinations can conceal parser defects | Test-design improvement deferred to Beta Stabilization | Beta Stabilization | Tests assert scenario and pair coverage, including conflicting declarations, across recorded seeds |

#### Remaining V1 compact-table and live-activity noncritical defects

These are known, noncritical V1 presentation limitations of the compact table and live Duration behavior. They are recorded for Beta Stabilization and do not change the current Phase 1 acceptance status. All reproductions are synthetic; no real Forge or OpenCode history is required.

**Independent Phase 1 QA: CHANGES REQUIRED.** The implementation is not ready for visual acceptance; TBL-BETA-001 and TBL-BETA-005 contradict the requested completion and frozen-duration behavior. The change-log implementation description below records the worker's initial report, not acceptance of those claims.

| ID | Reproduction | Impact | Milestone |
|---|---|---|---|
| `TBL-BETA-001` | An UNKNOWN job has Doing and Done derived from the same text record followed by a stop/end-turn step-finish; a later step-start alone does not replace Doing. Roleless text may also produce Done. | An intermediate result can be labeled a completed agent result indefinitely until a later eligible Doing record appears; the heuristic does not establish Forge job completion or assistant authorship. Independent QA requires changes. | Beta Stabilization |
| `TBL-BETA-002` | Change the system clock while a RUNNING or BLOCKED job is displayed and wait for the shared one-second dispatcher tick. | Live Duration can jump forward or backward; a backward jump is clamped to `0s`. Duration is not monotonic and is not a liveness proof. | Beta Stabilization |
| `TBL-BETA-003` | An agent-reported status is unavailable (`N/A`) and the job has a historical `Doing` excerpt. The Reported Status cell shows `• ` plus that activity. | Observed activity is presented in the status column with an unverified tooltip; a historical excerpt can be mistaken for current status. The value is never treated as authoritative and the complete text remains available in the tooltip. | Beta Stabilization |
| `TBL-BETA-004` | A finished job has a terminal or reported completion status but no usable `LastActivity`/Done source timestamp. | Duration stays `N/A` instead of guessing a finish time from the observation clock. The freeze is safe, but the user cannot see an observed span for that job. | Beta Stabilization |
| `TBL-BETA-005` | An UNKNOWN job has a Done excerpt with no source timestamp and no aggregate LastActivity; observe the unchanged record across successive scans. | The fallback uses each scan's ObservedAt, so the supposedly frozen duration can increase without new activity. Independent QA requires changes. | Beta Stabilization |
| `TBL-BETA-006` | Display a RUNNING or BLOCKED job whose metadata no longer reflects actual execution, or an active job whose metadata remains UNKNOWN. | Stale active metadata can drive the timer and spinner indefinitely; an active UNKNOWN job has neither. Metadata alone does not confirm current liveness. | Beta Stabilization |
| `TBL-BETA-007` | Expand the jobs table: Project is fixed at 90 px and Reported Status remains fixed at 140 px. | Freed space is not allocated to Reported Status; the spinner further reduces available text width. | Beta Stabilization |

#### V1 Live Activity history — bounded transient limitations

The Live Activity tab is a job-isolated, runtime-only projection built from the existing guarded reader parse of the already-read bounded window. Rows represent the reader's eligible Text/tool activity parts, not step-start/step-finish lifecycle records. It does not add a reader, persist content, query OpenCode stores, or infer completion. These limitations are noncritical for V1 and are recorded for Beta Stabilization; the safe behavior is intentionally conservative. All reproductions are synthetic and require no real Forge or OpenCode history.

| ID | Reproduction | Impact | Milestone |
|---|---|---|---|
| `LAH-BETA-001` | Start monitoring on a job with more than 64 KB of log, then let all previously visible text/tool records scroll out of the bounded follow-up window while new records arrive. | History cannot prove byte overlap with the prior buffer, so it is cleared and reseeded from the current window. Older rows are unavailable until Beta Stabilization provides durable/proven area coverage. | Beta Stabilization |
| `LAH-BETA-002` | Append more than 64 job-specific parts while monitoring; then restart DSMonitor. | Only the newest 64 retained parts (and at most 4,000 sanitized characters per part) are kept during one run; restart clears the transient history because it is never restored from SQLite or written to monitoring logs. Complete history is not claimed. | Beta Stabilization |
| `LAH-BETA-003` | Replace/truncate the source, change its verified physical identity/generation, reduce its captured length, or produce a non-overlapping reread. | The entire prior per-job history is cleared rather than mixing unproven records; the new successful read seeds fresh rows. | Beta Stabilization |
| `LAH-BETA-004` | Repeat the same exact session/message/part identity after its first record, including an older reread. | The newest physical record wins and older rereads do not overwrite it. Every previously retained record still inside the new bounded window must reappear at its exact offset with the same identity and digest; any mismatch resets the buffer to the current read because append-only continuity is no longer proven. Source races remain possible. | Beta Stabilization |
| `LAH-BETA-005` | A text part has no `state.time.start/end` pair, or a tool record lacks a valid same-part pair. | Duration is `N/A`; no job duration or inferred timing is used. Only the exact same-part tool start/end pair is displayed as an observed entry duration, and it is not proof of completion. Negative durations and timestamps outside the representable Unix-millisecond range are rejected rather than rendered as zero. | Beta Stabilization |
| `LAH-BETA-006` | An older row has no exact-bound current reported status, while the newest text does. | The older row displays its own observed text (not the newest status) and the newest row may display the exact-bound unverified reported value. Switching the reported-status target clears the prior binding; no value is carried to another identity. | Beta Stabilization |
| `LAH-BETA-007` | A RUNNING job's newest Doing record is replaced by a tool/step event, gains explicit same-part terminal evidence, or the job becomes BLOCKED/UNKNOWN. | The active spinner appears only when monitoring is active, Forge metadata reports an active job, and no job-scoped step-finish is at or after the latest observed activity. A running job with no Doing record still qualifies. Exact same-part tool-end evidence and an out-of-order terminal step-finish stop the row spinner by physical source offset. It was never evidence of worker liveness. | Beta Stabilization |
| `LAH-BETA-008` | Recovery falls back to a metadata-only or legacy whole scan, a whole scan throws, or a job's committed aggregate/coverage read fails. | The transient per-job history is cleared because no guarded source read proved continuity. A whole-scan failure publishes neutral unavailable activity (no cached Doing/Done/history/reported status) instead of the previously published snapshot, and an omitted job's activity buffer is cleared. | Beta Stabilization |
| `LAH-BETA-009` | A source appends records without newline termination, or a record is partially visible at the start/end of a bounded window. | Only fully newline-terminated records are admitted. A partial tail can delay a row until a later scan; no partial text is published. Existing append-only source and read-race limitations remain. | Beta Stabilization |
| `LAH-BETA-010` | Append another record for the same session/message/part identity with a different timestamp. Existing B2 ingestion rejects the batch as conflicting immutable timestamp metadata and reports conflict coverage. | The Live Activity history is cleared for that job rather than reading around the ingestion conflict; the UI shows the neutral unavailable state. Same-timestamp rereads update by newest physical offset (tested), but producer identity reuse with changed immutable metadata is a source-integrity conflict, not a supported history update. This preserves the existing ledger contract and is not weakened for V1. | Beta Stabilization |
| `LAH-BETA-011` | Select a text activity whose source record exceeds the 4,000-character history-preview bound. | Activity History keeps its bounded 4,000-character preview. The Observed Activity viewer shows the complete guarded A1 text without that preview cap; if A1 is incomplete or unavailable, a short neutral indication is shown and the text is never claimed complete. | Beta Stabilization |
| `LAH-BETA-012` | Activity text has multiple lines, an initial `##Status`/`##Result` marker, a leading `Status:` label, a leading bold span, or a first plain sentence; an initial line is empty; or other Markdown status/result syntax is used. | The Forge jobs table cell/tooltip and the corresponding Activity History row use the same normalized first line: an initial `##Status`/`##Result` marker with optional whitespace/colon and a leading `Status:` label are removed, a leading `**...**` span ends at its closing marker and preserves internal punctuation, a plain first line ends at its first sentence period, and no later lines leak through. A missing closing `**` is preserved safely. An empty first line falls back to the existing owned reported value; no unrelated/job-mismatched source is mixed. The A2 parser and raw selected-text viewer are unchanged. | Beta Stabilization |
| `LAH-BETA-013` | The selected activity record falls outside the current bounded window/256 KiB retrieval, the source changes, or ownership/UTF-8/role guards fail. | The existing guarded retrieval reports Incomplete, Unavailable, or Unsafe. The UI shows the bounded observed text with that explicit reason and never substitutes another part or claims completeness. | Beta Stabilization |

#### Original Request attribution — V1 beta limitation

The V1 Original Request field is a read-only, in-memory display for the selected job. It queries only the selected job's verified log-derived OpenCode session through `opencode api get`; it never scans another job/log or uses another Forge reader. Without observed assistant evidence it requests only user messages, reads every bounded page, shows exactly one as best-effort singleton, and leaves multiple messages empty/ambiguous. It never selects by timestamp: job start time is not an attribution input.

When the selected job's guarded, transient Live Activity projection is healthy and carries structured message ids for the same job, session, and source, the service reads one unfiltered ascending message stream (`order=asc` first page, cursor-only continuation) and requires complete bounded pagination, exact ordinal distinct ids, and every observed id to be an API assistant message. Role-verified assistant ids and roleless structured part ids are preserved as candidates; pinned OpenCode 2.2.6 payloads can omit an explicit role while retaining `part.messageID`. The API role check rejects a candidate observed as a user message, and an explicit user-role JSONL record is not admitted as evidence. The service tracks the latest preceding user by provider order (not timestamps) and selects that user only when all observed anchors share the same non-null predecessor. Missing, mismatched, non-assistant, conflicting, or duplicate stream evidence never falls back to the singleton heuristic and yields an empty ambiguous field; duplicate evidence/API ids and pagination/API failures yield unavailable. Other API message variants are ignored without reading their contents. Duplicate consumed JSON properties (`data`, `cursor`, `id`, `type`, `text`, `time`, `created`, `files`, `previous`, `next`) are rejected rather than resolved last-key-wins; unknown payload properties and legitimate other message types remain accepted. The selected text is exact and bounded; responses and errors are never logged.

An unavailable monitoring projection or failed scan is an explicit unavailable evidence state, distinct from a healthy no-anchor projection: it clears and invalidates prior request text/cache/in-flight results and does not query or retry from stale session metadata until a healthy observation returns. A healthy projection with no anchors still follows the normal best-effort singleton rule.

This observed correlation is still an inference: provider ordering is used to infer the preceding user, and the evidence ids come from a bounded Live Activity window. It is not authoritative verification of job ownership and must not be used for workflow, completion, cost, or audit decisions. Singleton results can still belong to another job sharing the session. Results are cached in bounded memory keyed by job, session, exact evidence fingerprint (including healthy/conflicting/unavailable state), and any explicit verified user id; evidence, association, start, or projection-health changes invalidate the job, and stale async results are ignored.

| ID | Reproduction | Impact | Milestone |
|---|---|---|---|
| `OR-BETA-001` | Two Forge jobs share one OpenCode session containing exactly one user message. Selecting either job displays the same request with a best-effort singleton label. | A job can display a request that belongs to another job using the same session. The label is explicit, the field is read-only, and no ownership or completion claim is made. Exact observed correlation is attempted only when the selected job has verified assistant evidence for the same session. | Beta Stabilization |
| `OR-BETA-002` | A session contains multiple user messages, no exact observed assistant correlation, and no usable explicit verified user id. | The field is empty and the attribution says ambiguous rather than guessing between candidates. Timestamp-nearest selection has been removed; repeated selection may re-query because ambiguity is not cached. | Beta Stabilization |
| `OR-BETA-003` | The OpenCode CLI is unavailable, times out, emits malformed/incomplete JSON, changes cursors inconsistently, or the response/text exceeds the bounded limits. | The field stays empty, no response or error text is logged, and no failure is cached. A transient failure can be retried by reselecting the job. CLI availability and the installed API shape remain runtime assumptions. | Beta Stabilization |
| `OR-BETA-004` | Two Forge jobs share a session with multiple user messages, and a job's metadata start time is uniquely closest to a message belonging to another job. | Historical independent QA finding. The V1 nearest-timestamp heuristic has been removed and start time no longer selects a request. Without exact observed assistant evidence the field remains singleton-only or ambiguous, so the shared-session misattribution risk is reduced but not eliminated. | Beta Stabilization |
| `OR-BETA-005` | A job has observed structured message ids, but the provider stream omits an id, reports it as another type, duplicates an id, or places the anchors before different users. A roleless JSONL part may also belong to a user message. | The field remains empty and ambiguous (or unavailable for duplicate/pagination failures); contradictory evidence never falls back to a singleton guess. Roleless ids are only used when the API confirms an assistant message; explicit user-role JSONL records are not evidence. Provider ordering is an inference and is not authoritative job ownership verification. | Beta Stabilization |
| `OR-BETA-006` | Monitoring fails or a Live Activity projection is unavailable/unsafe while a prior request result or query exists. | Prior text is cleared immediately, the job is invalidated, and no query or retry is made from stale session metadata until a healthy guarded observation returns. A healthy projection with no anchors still permits the normal best-effort singleton. | Beta Stabilization |

Prior independent QA findings are preserved above. No independent QA has accepted the observed-correlation behavior; `OR-BETA-001` and `OR-BETA-003` remain open limitations even where exact observed correlation succeeds. The observed correlation also depends on a bounded Live Activity window and on the installed OpenCode flat-message response shape, so it cannot prove ownership for assistants outside that window or for a different API version.

Noncritical residual limitations: roleless structured ids can belong to user messages and are rejected only by the API role check; repeated selection re-queries ambiguous/unavailable results because they are not cached; the health flag depends on the guarded Live Activity reader and monitoring service setting it correctly; duplicate-property rejection covers consumed properties only, so unknown payload duplicates remain tolerated; the API version/shape and live CLI behavior are not exercised by these synthetic tests.

**Historical remediation evidence for independent review findings (2026-11-09; status at that time).** Later B2 acceptance is recorded in section 1.11; final Phase 1 acceptance remains pending:

- **P1 — Path confinement — IMPLEMENTED.** Added `Services/SafeFileAccess.cs` and routed metadata parsing, tail polling, preview reads, and discovery root validation through guarded read-only opens. It rejects reparse points before opening (including root/ancestors), validates the opened handle's final DOS path against the resolved trusted root, checks the actual handle for reparse attributes, preserves `FileShare.ReadWrite | FileShare.Delete`, and fails closed with warnings. Temporary junction tests cover escape rejection, root-junction discovery refusal, and a runs-root replaced by a junction after reader construction. The file-symlink test was skipped in the earlier environment and later passed separately in the user's normal Windows session.
- **P2 — Event loss — IMPLEMENTED / PENDING INDEPENDENT QA.** Removed the per-poll 500-event drop from `ForgeLogTailReader`; byte/line bounds remain, dedupe/cursor behavior is preserved, and all accepted events are returned for logging. Tests cover 500/501/800-event batches and successive 300/300/200 appends. End-to-end `Run_LargeBatch_LogsAllEventsWhileSnapshotRetainsNewest500` confirms 800 `log_event` entries in `.jsonl` while the snapshot/UI remains capped at 500.
- **P2 — Timeline order — IMPLEMENTED / PENDING INDEPENDENT QA.** `JobRowViewModel` now uses a stable newest-first timestamp ordering, no `ObservedAt`/`EventKey` tie-break, and preserves original source order for equal/missing timestamps. Tests cover out-of-order timestamps, equal/missing timestamps across batches, and 500/501/800 retention boundaries. The service snapshot now preserves source order rather than re-sorting by key.

**Remediation validation:** Baseline before this final correction: 99 total, 98 passed, 1 skipped; build 0/0. Final build passed 0/0. Prior full suite: 102 total, 101 passed, 1 skipped (file-symlink privilege); all original and prior passing tests preserved. Targeted timeline policy/service→VM checks passed 27/27. The real symlink fixture was skipped then with Win32 1314 and subsequently passed in the separate manual run recorded above; the full suite was not rerun.

**Repair limit:** prior repair history remains exhausted and was not reset. The final focused correction was used for persistent timeline ambiguity mode and the 502-event partition regression. **Correction 2/2 is now consumed; zero correction attempts remain.** No further repairs or workers will be started.

### 1.6 Independent remediation review — 2026-11-09

**Verdict at that review: CHANGES REQUIRED.** The preceding authorized bounded task was finished; no implementation was delegated during that review. All 65 original tests still passed, and review found no deleted or weakened original tests. The suite included one environment-skipped symlink test, so no-skipped acceptance remained unmet. The bounded follow-up below supersedes the timeline portion of this historical verdict.

| Check | Independently observed result |
|---|---|
| `dotnet build DSMonitor.slnx` | Passed; 0 warnings / 0 errors |
| Targeted path, timeline, and batch filters listed above | 17 passed / 1 skipped / 18 total |
| `dotnet test Tests/DSMonitor.Tests/DSMonitor.Tests.csproj --no-build` | 82 passed / 1 skipped / 83 total |
| Successive-batch end-to-end log reproduction | All 800 events in both actual `.jsonl` and `.log`; UI capped at 500 |
| Junction escape, root junction, root replacement | Passed; no concrete remaining path-confinement defect found |
| File-symlink fixture | Unverified: independent Win32 attempts with flags 0 and 2 returned error 1314; .NET likewise reported missing privilege |

**Remaining findings after the bounded ordering/symlink task (2026-11-09):**

- **P2 — Newest timestamp events lost from retained display — IMPLEMENTED / PENDING INDEPENDENT QA.** Shared `TimelineOrdering` applies retention before service selection and again before VM display. All-known-only histories retain the 500 newest timestamps regardless of arrival. The former 500→501→800 descending-arrival reproduction passes through the actual service→view-model path.
- **P2 — Ambiguous mixed timestamp ordering — IMPLEMENTED / PENDING INDEPENDENT QA.** Missing timestamps are source-order barriers, not `MinValue` sentinels. Known segments sort stably newest-first; equal timestamps preserve source order. A per-job “has observed ambiguous timestamps” flag is persisted for the monitoring run and carried in each `ForgeJobSnapshot`; once set, retention stays in source-window mode even after the barrier leaves the visible window. The independent 502-event repro now yields the same 500..1 window for single-poll and 501+1/1+501/250+252/500+2 partitions; all 502 events remain in both logs.
- **File symlinks — VERIFIED / PASSED.** The earlier test was skipped after `CreateSymbolicLinkW` returned Win32 **1314** (`ERROR_PRIVILEGE_NOT_HELD`) with flags 0 and `SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE`. The user subsequently enabled Developer Mode, confirmed a real file symlink with `mklink`, and ran `OpenRead_FileSymbolicLink_FailsClosedWhenSupported` in normal, non-administrator Windows PowerShell: 1 passed, 0 failed, 0 skipped. The test creates a link inside the runs root to a file outside it and asserts that production `SafeFileAccess.OpenRead` refuses it, returns no stream, and the production log reader does not read its content. The later Codex sandbox run hung and was stopped without a result; it is not the passing run. No elevation, software installation, dependency download, or settings changes occurred during the manual validation.

**Agreed ordering policy (applied before service retention and VM display):** source-order barriers are missing timestamps. Each adjacent known-timestamp segment sorts newest-first with stable equal-timestamp source order. A history containing only known timestamps retains the 500 newest regardless of arrival. Once any missing timestamp has been observed for a job during the monitoring run, the most recent 500-event source window remains the retention mode even if that barrier is later evicted from the visible window. The bounded per-job ambiguity flag prevents partition-dependent policy switches; ambiguous events are never reordered across a missing barrier.

**Symlink manual gate: PASSED.** User-reported `dotnet test Tests/DSMonitor.Tests/DSMonitor.Tests.csproj --no-build --no-restore --filter "FullyQualifiedName~OpenRead_FileSymbolicLink_FailsClosedWhenSupported" --logger "console;verbosity=normal"` completed in normal non-administrator Windows PowerShell with 1 passed, 0 failed, and 0 skipped. The later bounded dual-window retention implementation is recorded in section 1.7; independent acceptance of the timeline correction remains pending.

Earlier implementation/pending-review notes in section 1.5 describe worker evidence; this bounded remediation supersedes their timeline labels but does not constitute independent acceptance. Event-loss remains independently resolved. Junction protection remains independently verified; real file-symlink validation subsequently passed as reported above.

### 1.7 Bounded timeline retention remediation — 2026-11-09 (latest)

**Status: IMPLEMENTED / PENDING INDEPENDENT SOL QA (SOLE CORRECTION CONSUMED). Phase 1 remains IN PROGRESS / NOT ACCEPTED.**

**Root cause.** The service applied all-known newest-timestamp retention after every poll. In `[known502, ..., known2, missing]`, a `501 + 1` partition trimmed `known2` before the later missing timestamp switched the history to source-window mode. A per-job ambiguity flag cannot recover an event already discarded, so retained IDs depended on poll boundaries.

**Correction.** Added `Services/TimelineRetentionBuffer.cs`, a per-job bounded accumulator that maintains two source-ordered candidate sets incrementally:

1. the most recent `capacity` (500) source events, used once any missing timestamp has ever been observed; and
2. the `capacity` newest timestamped events, used while every observed event has a timestamp.

The monitoring service now feeds unique accepted events into this buffer and publishes only the selected bounded set (≤ 500) in `ForgeJobSnapshot.Events`. The existing monotonic per-job ambiguity flag still drives view-model source-window mode, but it is no longer relied on to reconstruct discarded history. The view model still applies the shared display ordering: missing timestamps are barriers, each adjacent known segment is newest-first, and equal timestamps keep source order. Out-of-order timestamps are sorted for display; source order is preserved in retained snapshot output and across barriers. No timestamp is invented.

**Sol QA replay correction (sole correction consumed).** The 10,000-key recent dedupe windows in the reader and service are bounded, so a high-timestamp event can remain in the newest-500 candidate window after its key has been evicted. `TimelineRetentionBuffer` now exposes `ContainsRetainedKey`; the service rejects an incoming event when its key is either in the recent 10,000-key window or still present in either retained candidate window. This prevents an identical replayed line from appearing twice in the snapshot or view model without unbounded history retention. The service also tracks accepted unique events per job for `ParsedEventCount`, so a rejected replay does not inflate the displayed accepted count. Replaying an event that is neither recent nor retained can still be accepted and logged again after eviction; broader exactly-once semantics across unbounded history are not possible under bounded memory and are not claimed. The tail reader remains independently bounded. At the time of this correction a re-emitted line could still update reader-internal aggregates (for example cost or latest-session state) before the service rejected it; section 1.8 supersedes that specific limitation by moving acceptance ahead of those aggregate updates. Exact idempotency beyond the bounded recent and retained windows remains unavailable and is not claimed.

**Deterministic retention policy.**
- Missing timestamp: source-order barrier; never sorted as `MinValue`.
- Equal timestamps: source order is preserved; at an all-known retention boundary the earlier source event wins the stable descending tie.
- Out-of-order timestamps: retained/displayed by timestamp, not arrival.
- All-known histories: keep the 500 newest comparable timestamped events regardless of arrival.
- Once any missing timestamp is observed for a job: keep the most recent 500-event source window, even after that barrier leaves the window.
- The same full source therefore yields identical retained IDs and visible order for single, individual, 500+remainder, 501+remainder, uneven, randomized, and intermediate-prefix partitions.

**Memory bound.** Persistent retained state is at most `2 × 500 = 1000` event references per job in the retention buffer (source window + newest candidates), a retained-key set of at most 1000 keys, and the pre-existing bounded recent dedupe sets (10,000 service keys and 10,000 reader keys). Snapshot and UI remain capped at 500. The exhaustive test asserts the 2×500 retained-event bound and the targeted memory test asserts exactly 1000 when both windows are full and 500 for an all-missing history. These persistent limits are distinct from bounded temporary per-poll memory: `newlyAccepted`, the `combined` candidate list, reader byte/char buffers, and the poll result scale with bytes newly read (up to the configured 512 KiB read bound) and are released after the poll. They are not part of the retained-history bound.

**Validation (local, not independent acceptance).**

- Independent full-history oracle added in `tests/DSMonitor.Tests/TimelineRetentionOracleTests.cs`; it does not call production ordering/retention code.
- Pre-fix dual-window oracle regression failed on the `501 + 1` partition at retained index 18; post-fix all partitions match the oracle.
- Pre-fix >10,000-key replay regression failed with two snapshot entries for the replayed high-timestamp key; post-fix the snapshot, view model, and JSONL event timestamp each contain it exactly once, with 10,002 accepted events in both persistent logs (10,001 originals + sentinel).
- Oracle suite: 6/6 passed, including the service→view-model >10,000-key replay regression.
- Late-missing 502 source: single, 501+1, 1+501, 500+2, 250+252, uneven, and deterministic randomized partitions all match the oracle at each batch prefix and final state; 502/502 accepted events in each job's `.log` and `.jsonl`.
- Actual service→view-model: 32 shape jobs covering 499/500/501/502/800 events with ascending, descending, out-of-order, equal, all-missing, multiple missing, and first-missing positions 1/499/500/501/502/800; exact retained IDs and visible order match the oracle.
- Exhaustive buffer matrix: 229 valid scenario/partition combinations (37 scenarios; 5, 6, or 7 valid partitions depending on event count); retained IDs and visible order match the oracle at every intermediate prefix, not only the final batch.
- Real `ForgeLogTailReader`: 502 one-line appends, repeated polls, duplicate line, cursor advancement, and dedupe all match the oracle. An additive reader regression confirms the bounded 10,000-key window re-emits a replayed old line, which the service then suppresses via retained-key dedupe.
- Baseline immediately before the original dual-window correction: build 0/0; 102 passed / 0 skipped / 102 total. After dual-window: 107 passed. After this replay correction and the additive reader regression: build 0/0; targeted timeline/oracle 33/33; integration/security 37/37; full suite 109 passed / 0 skipped / 109 total.
- No existing test assertion was modified, deleted, disabled, skipped, or weakened. One additive reader regression was added to the existing reader test class. All 107 post-dual-window tests and all 65 original tests remain passing.
- The independently triggered Sol testhost failed to connect to localhost with `SocketException 10013`; no sandbox, permission, or environment bypass was attempted and the identical failing command was not retried. That run supplied no acceptance evidence; the local results above are the worker's actual executed checks.

**Correction allowance.** The sole authorized focused correction for this task was consumed by the independent-Sol-QA replay/dedupe finding and the every-prefix display assertion. **Zero correction attempts remain; no further correction cycles will be started.** Phase 1 remains IN PROGRESS / NOT ACCEPTED pending independent QA acceptance. No other workers were started.

**Phase completion gate:** independent QA must accept the timeline correction. The real file-symlink manual gate passed. Phases 2–4 remain NOT STARTED. No interactive computer use or screen access occurred during remediation.

### 1.8 Bounded cost-bearing replay acceptance remediation — 2026-11-09 (latest)

**Status: INDEPENDENT SOL QA — CHANGES REQUIRED. Phase 1 remains IN PROGRESS / NOT ACCEPTED.**

**Root cause.** The contract already bounded recent keys (10,000 per reader and per service) and retained timeline keys (at most 1,000 per job across the two candidate windows). An old cost-bearing event can remain retained after its recent key expires. The reader is allowed to re-emit that line, and the service then rejects it as retained/recent. However, the reader had already mutated its projected aggregates while processing the line, before the service decision: `TotalCost`/`HasCost`, `LatestSessionId`, `LastActivity`, and `TotalUniqueEvents`. The service therefore rejected the timeline/count/log duplicate but still projected inflated cost (and could revert the displayed session or activity). This was a source-derived independent Sol QA finding, not previously runtime-reproduced.

**Implementation.** `ForgeLogTailReader.Poll` now accepts an optional acceptance predicate. After the reader's own bounded dedupe and before any cost, session, activity, or accepted-unique-count mutation, the predicate decides whether the event is part of the accepted poll result. The monitoring service supplies a predicate built from the service's recent 10,000-key set, the retained-key set in both 500-event candidate windows, and keys already accepted earlier in the same poll. Therefore:

- a rejected recent/retained duplicate has no effect on projected cost, session, activity, accepted count, retained timeline, view model, or either persistent monitoring log;
- a legitimate new event updates once when accepted;
- the 500-event source and newest-timestamp candidate windows remain unchanged, as do source/timestamp partition invariance, byte cursors, and incremental reads; rotation/path-continuation and replay limitations are described below;
- no unbounded ID collection, larger dedupe window, or timeline redesign was introduced.

**Unresolved identity limitation.** An event that is neither in the recent 10,000-key sets nor in either retained window can be replayed and accepted again. Finite identity retention alone cannot guarantee exact idempotency for arbitrary replay; the requested replay behavior is therefore not fully met. Previous documentation of this limitation does not authorize narrowing the user's acceptance criteria. No unbounded ID history or larger arbitrary dedupe window was added.

**Independent Sol QA: CHANGES REQUIRED.** Sol independently reproduced a fully evicted replay increasing cost from `0.75` to `1.50`, reverting the continued session, and adding an event to both logs. Sol also reproduced changing a job's log path causing accumulated cost and session to become null when the new reader rejects an already-seen event. A retained high-timestamp replay, by contrast, preserved cost, session, accepted count and both logs. The independent serial `--no-restore` build passed with 0 warnings/errors, and `git diff --check` passed. An independent test invocation produced no results for several minutes and was interrupted; the 114/114 full-suite result remains worker-reported. The one correction authorized for this task remains unused; resolve the bounded-identity incompatibility before changing the behavioral contract or starting further remediation. Phase 1 remains IN PROGRESS / NOT ACCEPTED; Phases 2–4 remain NOT STARTED.

**Memory bound.** Persistent state is unchanged: at most `2 × 500 = 1000` retained event references plus up to 1,000 retained keys per job, and the pre-existing two 10,000-key recent sets. The new `acceptedThisPoll` set is temporary and bounded by the events parsed from at most the configured 512 KiB read bound; it is released after the poll. Snapshot and UI remain capped at 500.

**Deterministic coverage added.** Tests use actual `ForgeLogTailReader` and `ForgeMonitoringService` paths, with no paid model sessions:

- one legitimate cost-bearing event, an immediate duplicate, a distinct legitimate identity, incremental append, and repeated polling;
- a retained cost-bearing replay after the visible timeline has exceeded 500 events;
- a retained cost-bearing replay after the recent dedupe key has expired beyond 10,000 keys, with `0.750` cost before and after and the continuing session preserved;
- a cost-bearing log replacement that must add only the new cost (`0.200` then `0.500` total);
- a direct reader acceptance-gate test proving a rejected cost event leaves `TotalCost`, `HasCost`, `LatestSessionId`, `LastActivity`, and `TotalUniqueEvents` untouched;
- both `.log` and `.jsonl` event counts, plus high-timestamp occurrence counts, are asserted after Stop;
- all existing timeline/oracle partition, source-order, ambiguity, cursor, dedupe, and security regressions remain passing.

**Executed evidence (worker, not independent acceptance).**

- Baseline before this remediation: build 0 warnings / 0 errors, exit 0; full suite 109 passed / 0 skipped / 109 total, exit 0.
- Pre-fix new cost suite: 1 passed / 2 failed / 3 total, exit 1. The definitive failure observed cost `1.5` where `0.75` was expected after the retained replay. The other failure was test-only live-log reading and was corrected before production changes.
- Post-fix new cost/replacement/reader-gate suite: 5 passed / 0 failed / 5 total, exit 0.
- Timeline/oracle + cost-replay targeted suite: 36 passed / 0 failed / 36 total, exit 0.
- Integration/security/logging targeted suite: 47 passed / 0 failed / 47 total, exit 0.
- Full suite: 114 passed / 0 skipped / 114 total, exit 0; all 109 pre-remediation tests and all 65 original tests preserved.
- Final `dotnet build .\DSMonitor.slnx --no-restore`: 0 warnings / 0 errors, exit 0.
- `git diff --check`: clean, exit 0 (only Git LF→CRLF working-copy notices).
- No `SocketException 10013` or other infrastructure failure occurred in this remediation run. No sandbox, permission, environment, configuration, or security bypass was attempted.

**Correction allowance for this newly authorized task.** This is initial implementation only. The earlier timeline task's exhausted corrections are not reused or reset. At most one later focused correction is permitted if independent Sol QA identifies a concrete defect; no automatic repair or agent was started here.

**Unresolved risks / limits.** Exact once-only semantics across unbounded replay are not guaranteed. A replay outside both recent windows and both retained windows can still be accepted and logged. A reader recreated for a changed log path starts a fresh projected cost base while service dedupe state persists; that pre-existing behavior was not redesigned. Independent Sol QA has not accepted this remediation. Phases 2–4 remain NOT STARTED.

**Architecture gate — 2026-11-09.** Native source review confirms that the current contract cannot guarantee exact logical replay idempotency with the available bounded state. The parser constructs keys from optional session/message/part fields, type, timestamp, status, and a raw-line hash; the code does not establish a mandatory, stable, unique logical-event ID across continuations. A file identity and byte offset support incremental reads of the same physical file, but a duplicate appended at a new offset or copied into a replacement file has a different physical position. Replacement or truncation resets the cursor, and recent and retained keys eventually expire. An identical raw record can also be indistinguishable from a distinct legitimate occurrence without a source identity contract. Existing tests cover a retained replay and a replacement with a recent duplicate; they do not establish arbitrary replay idempotency.

The reader owns cost, session and activity aggregates, whereas the service owns accepted counts and bounded deduplication and timeline state. A path change replaces the reader but retains the service's accepted keys, so rejected old records cannot restore the new reader's empty aggregates. Preserving job-owned aggregates across reader replacement is a possible focused fix for path continuity, but it does not solve fully evicted logical replays. Exact arbitrary replay would require a reliable source identity and authoritative reconciliation or sufficiently durable identity history; a cursor alone suffices only with an enforced monotonic/no-replay source contract. Durable history can grow and adds storage and lifecycle concerns; an authoritative compact checkpoint needs a proven source guarantee. Restricting replay semantics or using a finite deduplication window would change the acceptance contract. No such change is authorized.

The architecture gate therefore stops the correction before production changes. The sole authorized correction remains unused; no new worker, tests, or independent QA were run for this gate. Prior independent Sol verdict remains **CHANGES REQUIRED**; the prior worker-reported 114/114 suite was not independently reproduced. Phase 1 remains **IN PROGRESS / NOT ACCEPTED** and Phases 2–4 remain **NOT STARTED**. The user-verified real file-symlink result remains valid.

### 1.9 B1 durable SQLite event-ledger foundation — 2026-11-09 (latest)

**Status: IMPLEMENTED / PENDING INDEPENDENT SOL QA. Phase 1 remains IN PROGRESS / NOT ACCEPTED. Phases 2–4 remain NOT STARTED.**

**Scope.** Added a separate byte-oriented persistence foundation with an independently testable seam. The live production monitoring pipeline was not cut over to the ledger; timeline retention, monitoring-run logging, PathSafety/SafeFileAccess behavior, status handling, and UI remain unchanged. No Phase 2/3, B2 aggregate migration, or B3 outbox work was started. The only new package is the stable `Microsoft.Data.Sqlite` **10.0.14** reference, restored with its transitive NuGet dependencies (`Microsoft.Data.Sqlite.Core`, `SQLitePCLRaw.*`) under the explicitly authorized restore.

**Changed files.**
- `Persistence/EventLedger.cs` — schema-versioned SQLite foundation, guarded transactional ingest, physical source generation and cursor handling, bounded framing/decoding, safe metadata persistence, and read APIs.
- `Persistence/EventLedgerModels.cs` — schema version, identity, dispositions, ingest/storage results, bounded options, and approved storage paths.
- `tests/DSMonitor.Tests/EventLedgerTests.cs` — 31 deterministic isolated tests after the post-QA correction.
- `DSMonitor.csproj` — `Microsoft.Data.Sqlite` 10.0.14.
- `docs/Project Requirements.md` — `LEDGER-001..020`, updated `SEC-011`, and `TEST-021`.

**Schema version 1.**
- `ledger_meta` — schema version marker.
- `jobs` — durable job identity.
- `source_generations` — `(generation_id, job_id, source_path, volume_serial, file_index, creation_utc_ticks, first/last seen, predecessor)`.
- `source_cursors` — committed offset and monotonic cursor version.
- `source_records` — complete `[start_byte, end_byte)` ranges with payload length, explicit disposition, and a content digest used only for validation; unique on `(generation_id, start_byte)` with end/length/disposition/digest validation on retry.
- `source_events` — safe event metadata only; the diagnostic event key is not a unique identity. No raw model text, tool input/output, source content, credentials, or permission payloads are stored.

**Guarantees implemented.**
- Opening validates schema version, metadata, required tables, and required columns. Unsupported, incomplete, corrupt, or locked storage fails closed; existing data is never silently deleted, overwritten, migrated, or reinitialized.
- Physical identity is captured from the opened handle by `GetFileInformationByHandle` (volume serial, file index, creation time). Path strings and content hashes are not identity keys. Weak identity returns `AmbiguousSourceIdentity` before any mutation.
- Framing is performed on LF bytes before strict UTF-8 decoding. Partial trailing bytes never advance the committed cursor; multibyte content and boundaries are handled without decoding partial records.
- Every complete range receives an explicit `Accepted`, `Empty`, `MalformedJson`, `InvalidUtf8`, or `Overlong` disposition. Only accepted records create event metadata.
- Generation establishment, record/event insertion, and cursor advancement occur in one transaction. The cursor update is guarded by the expected committed offset and increments its version. A stale/conflicting update or any failure rolls back generations, records, events, and the cursor together.
- SQLite uses WAL, `synchronous = FULL`, foreign keys, bounded busy timeout, no pooling, and no application retry loop. Read bytes, line size, memory, and wait time are bounded.
- Replacement/copy with a different physical identity creates a new generation from byte 0. A same-identity rename may continue the generation. Restart/reprocess resumes at the committed cursor. A file shorter than its cursor returns `ContinuityUncertain` without mutation.

**Limitations (not acceptance claims).**
- Continuity assumes append-only growth for a verified generation. The ledger cannot prove undetected truncation-and-regrowth, in-place rewrite to the same-or-larger length, or operating-system file-ID reuse.
- Identical bytes at distinct offsets or generations are deliberately preserved; this is physical-range identity, not arbitrary logical-event idempotency.
- B1 does not solve all replay semantics and does not by itself satisfy legacy `ERR-006/007/008` replacement/rotation/deduplication requirements. The live tailer remains the Phase 1 behavior for those requirements.
- A complete record larger than the bounded transaction window fails closed as `RecordTooLarge`; no cursor advance occurs.
- Independent Sol QA has not reviewed B1. Self-checks are not independent QA.

**Validation evidence (worker-executed, not independent acceptance).**
- Baseline immediately before B1: build 0 warnings / 0 errors; full suite 114 passed / 0 skipped / 114 total, exit 0.
- Authorized restore: `dotnet restore .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj` succeeded with Microsoft.Data.Sqlite 10.0.14 and transitive SQLitePCLRaw 2.1.13 packages. All later build/test commands used `--no-restore`.
- B1 focused suite: 31 passed / 0 failed / 31 total, exit 0.
- Timeline/partition-invariance regression: 33 passed / 0 failed / 33 total, exit 0.
- Logging/integration/security regression: 47 passed / 0 failed / 47 total, exit 0.
- Full suite: 145 passed / 0 skipped / 145 total, exit 0; all 114 pre-B1 tests and all 65 original tests preserved.
- Solution build: 0 warnings / 0 errors, exit 0.
- `git diff --check`: clean, exit 0 (only Git LF→CRLF working-copy notices).
- No existing test assertion was modified, deleted, disabled, skipped, or weakened. All 31 B1 tests are additive and use isolated temporary directories.

**Correction allowance.** This is initial B1 implementation. One focused correction may be requested after independent Sol QA in this same session; no additional repair task and no reset of prior history. The earlier timeline task's exhausted allowance was not reused or reset.

### 1.10 B1 post-QA correction — 2026-11-09 (latest)

**Status: IMPLEMENTED / PENDING INDEPENDENT SOL REVALIDATION. Phase 1 remains IN PROGRESS / NOT ACCEPTED. Phases 2–4 remain NOT STARTED. The sole authorized post-QA correction is consumed; no additional repairs or tasks were started.**

Independent QA returned **CHANGES REQUIRED** for four B1 findings. This correction addresses those findings without changing the live monitoring pipeline, B1 scope, packages, security configuration, or prior test assertions.

1. **Schema integrity / silent event loss.** The previous open validation checked names and columns only, so an unexpected `BEFORE INSERT` trigger using `RAISE(IGNORE)` could be accepted and silently suppress event rows. Validation now compares the complete expected schema objects and normalized DDL: required tables/indexes must match exactly, and unexpected tables, indexes, views, or triggers are rejected before writes. Every expected generation/cursor/record/event insert and generation/cursor update now verifies one affected row; zero or unexpected counts throw and roll back the whole transaction. Regressions cover an unexpected trigger at reopen, a trigger added after open, a malformed table, and a constraint-deficient table; persisted records/events/cursor are verified unchanged after failure.
2. **Storage confinement.** Added `Persistence/LedgerStorageGuard.cs`. It normalizes and checks the approved root, creates/checks each directory component, rejects reparse points, and verifies existing database and `-wal`/`-shm` entries and opened-handle final paths stay inside the root before open and before writes. It does not modify permissions or use privileged link creation. Residual limitation: a concurrent actor able to replace a path between validation and SQLite's own open, or a pre-existing hard link, cannot be fully eliminated without OS-level isolation; the ledger fails closed whenever an escape/reparse point is observable, and no stronger guarantee is claimed.
3. **Bounded retrieval.** `ListRecords`/`ListEvents` now use keyset pagination with an explicit default limit of 500 and maximum of 5,000, deterministic ordering, and bounded result allocation. Existing one-argument calls remain bounded; no unbounded history materialization is permitted. Tests page through 650 records/events and verify limit failures.
4. **Real multibyte split.** Added a test that writes literal non-ASCII UTF-8 bytes (Japanese text, not JSON escapes), splits the file inside a multibyte continuation byte, verifies the first poll commits no record/cursor bytes, then appends the remainder and verifies full cursor advancement and decoded acceptance (`Event: 日本語`).

**Changed paths for the correction.** `Persistence/EventLedger.cs`, new `Persistence/LedgerStorageGuard.cs`, `tests/DSMonitor.Tests/EventLedgerTests.cs`, `docs/Project Requirements.md`, and this plan. No package or restore changes were required; all commands used `--no-restore`.

**Validation evidence (worker-executed; not independent validation).**
- B1 focused suite: **31 passed / 0 failed / 31 total**, exit 0.
- Timeline/partition-invariance regression: **33 passed / 0 failed / 33 total**, exit 0.
- Logging/integration/security regression: **47 passed / 0 failed / 47 total**, exit 0.
- Full suite: **145 passed / 0 skipped / 145 total**, exit 0; all 114 pre-B1 tests and all 65 original tests preserved.
- Solution build: **0 warnings / 0 errors**, exit 0.
- `git diff --check`: clean, exit 0 (only Git LF→CRLF working-copy notices).
- No existing test assertion was modified, deleted, disabled, skipped, or weakened. The 31 B1 tests use isolated temporary directories; real junction fixtures are unprivileged test-only reparse points and are cleaned up non-recursively.

**Independent QA history preserved.** The prior independent Sol run reported a passing serial build; a normal vstest invocation failed with a testhost connection issue; the in-process run reported 20 passes with 2 permission-blocked cases. Those results are not claimed as independent validation of this correction. Sol revalidation remains **PENDING**.

**Correction allowance.** The one authorized focused B1 post-QA correction is consumed. No additional repair task or agent was started, and prior history/allowances were not reset. The user-verified real file-symlink result remains preserved.

**Final independent Sol revalidation: CHANGES REQUIRED. B1 NOT ACCEPTED.** This verdict supersedes the pending-review labels and the implementation guarantees claimed above. Sol independently reproduced a schema-validation bypass: SQLite `LIKE 'sqlite_%'` treats `_` as a wildcard, so a trigger named `sqliteXdrop_events` is omitted from validation. An `AFTER INSERT` trigger deleting the inserted event produced a committed record and cursor 22 while the event table was empty. Affected-row checks do not catch that subsequent deletion. Sol also found by source inspection that a reparse-point ancestor above the approved root can redirect initial database creation before the final-path check, and that existing multiply-linked database or sidecar files are not rejected. These are distinct from the documented concurrent path-replacement race; rejecting observable multiple hard links is possible. Storage confinement and exact trigger rejection are therefore not established. No additional repair is authorized.

**Independent evidence and limits.** Serial `dotnet build .\DSMonitor.slnx --no-restore -m:1` passed with zero warnings/errors; `git diff --check` passed. Sol invoked existing ledger methods in-process: 27 passed, two replacement/rename cases were blocked by file-move permission errors, and two junction cases were deliberately not run after an earlier permission denial. This is not a successful MSTest run. Normal testhost runs previously timed out connecting, so the post-correction 31/33/47/145 results remain worker-reported, as do the initial B1 results of 22 ledger tests and 136 full-suite tests. The separate Forge worker reported junction fixtures passing in its execution environment; they do not establish the untested ancestor or hard-link cases. The schema trigger reproduction is independent evidence. The user-verified real file-symlink result remains valid.

**Subsequent separately authorized B1 security remediation.** The earlier `sqlite_%` filter treated `_` as a wildcard; affected-row checks did not detect an AFTER trigger deleting an event. The storage guard created the root before inspecting its ancestors and did not check hard-link counts. The remediation enumerates and validates the schema against an explicit allowlist, revalidates inside the immediate write transaction, checks transactional persistence postconditions, inspects existing ancestry before creation, and rejects observable reparse points and multiply linked database, WAL, SHM, and rollback-journal files. The live monitoring pipeline remains unchanged. An active namespace or hard-link replacement between validation and SQLite's pathname opens, including recovery and sidecar operations, remains a security limitation; these checks do not prove race-free containment. An existing nonempty but otherwise empty version-0 SQLite container is rejected; supported version-1 databases reopen.

**Evidence and independent review.** The worker reproduced the trigger defect before editing and reported 43/43 ledger, 33/33 timeline, 47/47 logging/security/integration, and 157/157 full-suite tests passing; build and diff check passed. The worker reported running junction tests through Forge despite the instruction not to repeat the previously denied junction operation through another execution path; those results are not independently verified and that operation was not repeated during review. Three existing trigger tests now expect earlier incompatible-schema rejection while retaining their preservation assertions; a preservation assertion was strengthened. Sol independently reviewed the changes, passed a serial no-restore build and eight focused in-process tests, and returned **APPROVED for the remediation with limitations**. An independent hard-link fixture creation failed; Sol did not retry it or run other hard-link fixtures. Ancestor/junction behavior was reviewed from source only, and real hot-journal recovery was not independently verified. The worker-reported full-suite results are not independent testhost results. Source/test/configuration fingerprints were unchanged during independent review. The prior real file-symlink integration result remains user-verified.

**Subsequent B1 recovery verification (not yet complete).** The user reports independently running the seven targeted real junction and hard-link tests in ordinary non-administrator PowerShell: build passed, 7 passed, 0 failed, 0 skipped. Fixture inspection confirms those tests require successful creation of the actual links. Two isolated process-interruption tests were then added: production-mode WAL recovery through the ledger, and a real rollback-mode hot journal that the ledger's read-only preflight refuses unchanged before a separate raw SQLite fixture step recovers it. The worker reported 2/2 recovery tests and 36 safe existing ledger tests passing before a subsequent test-host lifetime correction. Independent Sol review approved the corrected test source, but the independent test run aborted after the testhost could not connect; the corrected version has not been run. No alternate execution path was used to bypass that failure. The rollback fixture is outside the normal WAL mode and does not establish automatic ledger hot-journal recovery; neither test proves power-loss durability or interruption inside an actual ledger ingest.

**Final B1 acceptance — ACCEPTED.** The user reports running the corrected recovery tests in ordinary non-administrator PowerShell: **2 passed, 0 failed, 0 skipped**. Together with the user's **7 passed, 0 failed, 0 skipped** real junction and hard-link tests, the independently approved recovery-test source, and prior independent schema, transaction, and build reviews, this supplies the requested evidence for the B1 foundation. The earlier independent testhost timeout remains an environment limitation, not a separately passed run; earlier full-suite counts remain worker-reported. Fixture review confirms the junction and hard-link tests call Windows APIs and require successful link creation. The WAL test checks committed and uncommitted frames, cursor/event consistency, and resumed ingestion. The separate DELETE-mode test verifies a genuine hot journal, fail-closed ledger preflight, SQLite recovery, and subsequent ledger reopening.

Acceptance is limited to the documented B1 contract. `LEDGER-020` explicitly records the residual race between path validation and SQLite opens; `SEC-011` still prohibits writes outside approved locations, and race-free confinement is not claimed. Normal ledger mode is WAL (`LEDGER-014`); direct automatic ledger recovery of a rollback hot journal, arbitrary logical-event exactly-once processing, power-loss durability, and detection of every in-place source rewrite are not established. At B1 acceptance, the ledger was not connected to live monitoring and the replay and path-change defects remained unresolved; subsequent B2 acceptance addresses them within its supported identity contract (section 1.11). The previously user-verified real file-symlink result remains valid.

Phase 1 remains **IN PROGRESS / NOT ACCEPTED**; Phases 2–4 remain **NOT STARTED**. At this B1 decision, B2 and B3 had not started; B2 has since been accepted (section 1.11), while B3 remains not started. Earlier pending-review and B1-not-accepted statements above are historical and superseded by this final B1 decision.

---

### 1.11 B2 — Durable Aggregates and Live Pipeline Integration

**Final B2 acceptance — ACCEPTED with documented limitations.** The fresh v2 ledger integrates historical bootstrap and live monitoring with durable job aggregates, structured job-scoped logical identity, coverage and bounded retrieval. Independent Sol QA initially returned CHANGES REQUIRED for seven findings, then identified an uncovered fallback log on revalidation; two focused post-QA repairs were consumed. Final Sol revalidation returned APPROVED: serial no-restore build passed with zero warnings/errors, 38/38 directly invoked ledger/service/discovery methods passed in-process, and diff check passed. This was not a successful independent MSTest run: the earlier focused VSTest invocation aborted after testhost connection timeout. The worker reported 198/198 full-suite and 83/83 focused tests passing and a clean build; these remain worker-reported evidence. Unassociated fallback logs are reported as uncovered, not ingested. Source IDs are conditional on producer behavior; regenerated/reused IDs, missing history, undetected rewrites and B1's documented path race remain limitations. B3 owns crash-safe external-log delivery. Phase 1 remains IN PROGRESS / NOT ACCEPTED; B3 and B4 remain NOT STARTED.

The user subsequently reported that the complete B2 suite passed without errors in normal Windows PowerShell. The exact command, test count and skipped count were not supplied; this is user-reported validation.

**Historical implementation checkpoint — IN PROGRESS / INCOMPLETE at that time.** The DeepSeek worker reached its configured step limit before completing B2. It reported partial v2 ledger, parser, live-monitoring and test changes, a successful baseline build and an intermediate build before final test edits. The final tree and new tests had not then been compiled or run; v2 schema validation, service integration, rollback and security behavior remained unverified. Subsequent continuation, repairs and final acceptance are recorded above.

**Architecture addendum — APPROVED FOR IMPLEMENTATION.** The user has replaced v1-to-v2 migration with a fresh `%LOCALAPPDATA%\DSMonitor\Data\events-v2.db` and resumable historical Forge-log bootstrap. Existing B1 databases and sidecars are preserved. Bootstrap starts on Start Monitoring and shares bounded physical ingestion, job-scoped structured source identity, atomic aggregate/cursor/checkpoint transactions and the logical-event index with live monitoring. Coverage gaps and missing costs remain explicit. B3 owns durable external-log delivery. Source IDs support replay only within the stated producer-identity contract; no global uniqueness or arbitrary logical exactly-once guarantee is claimed. This addendum supersedes the earlier required migration and unapproved-gate descriptions below; historical findings are retained.

Implementation packages: structured identity and fresh v2 schema; safe discovery and resumable bounded bootstrap; atomic logical projection and coverage; live monitoring and bounded timeline restoration; B3 retrieval seam and regression validation. Acceptance includes interruption and restart, concurrent append, same-job copied replay, distinct same-cost IDs, shared-session job separation, >10,000-event replay, path change, identity and storage failures, partial/malformed sources, B1 database preservation, large bounded batches, timeline ordering, security and lifecycle regressions.

**Status: ACCEPTED. Goal:** Integrate the accepted ledger with live monitoring and persist cost, session and activity state under explicit source-identity and replay guarantees. **Dependencies:** accepted B1 and the approved architecture addendum above.

**Historical snapshot, superseded by B2 acceptance and the approved addendum above.** **Scope and boundaries.** `EventLedger` owns verified physical generations, complete records, safe event metadata and transactional cursors (`LEDGER-001..020`); it is isolated from live monitoring. `ForgeLogTailReader` owns cost, latest session, activity and unique-event projections; `ForgeMonitoringService` recreates readers on path changes and owns bounded acceptance and timeline state. The logger writes per-run outputs separately. Establish durable job aggregate ownership and persist observed cost and its presence, latest session, activity and any accepted count or other state required for compatible projections. The gate must define job-to-generation and record relationships, initialization for existing installations, and whether safe ledger metadata suffices or additional permitted state is needed. Do not persist raw source content or secrets. Preserve job separation and existing WPF projections.

For supported physical records, aggregate updates, accepted record/event disposition and cursor advancement must commit atomically or all roll back. Restore committed state after restart. Reader recreation must preserve valid job aggregates; validate continuity before associating a source with a job and handle a verified replacement as a separate generation under the approved policy. Surface storage, stale cursor, identity and continuity failures without publishing uncommitted updates; retain last valid state and recover only when safe. Preserve B1's fail-closed and bounded-read contracts.

**Historical snapshot, superseded by B2 acceptance and the approved addendum above.** **Identity and unresolved requirements.** A physical record is identified by verified generation and start offset, with existing consistency checks. A previously accepted physical record must not aggregate twice after memory eviction. Identical bytes at different offsets or generations are distinct physical records. A copied or newly appended logical replay has a different physical identity and may be indistinguishable from a legitimate identical occurrence; hashes are not identity. `DATA-015` and `ERR-006..008` require duplicate-free behavior across repeated reads, truncation and replacement using a stable event key, while B1 cannot establish arbitrary logical identity or detect every rewrite. The requirements document also retains historical B1 pending-review labels (`LEDGER-018` and section 13.1); the later acceptance in section 1.10 supersedes those status notes, without changing the requirements. The gate must resolve the identity tension with evidence and an authorized decision; broader requirements remain unresolved, not weakened or claimed satisfied. Truncation below the cursor and weak or ambiguous identity fail closed under B1; undetected rewrite/regrowth and active path-replacement races remain limitations.

**Historical snapshot, superseded by B2 acceptance and the approved addendum above.** **Pre-implementation architecture gate — REQUIRED, not approved.** Before implementation use Terra Medium → Sol High architecture gate → DeepSeek Flash implementation → independent Sol QA → Terra acceptance. Direct Sol High → DeepSeek delegation is permitted only if the active runtime and authorization support it. The reviewer must establish aggregate ownership, atomic boundaries, supported event identity, replay and continuity guarantees, migration and compatibility with version-1 data and strict schema validation, recovery and failure invariants, adversarial tests, and the B2/B3 boundary. Resolve requirement conflicts and storage/security implications before implementation; return to the gate if assumptions fail. This roadmap does not launch the workflow.

**Acceptance criteria (accepted within the documented limits above):**

- [x] Restart restores correct committed cost, session and activity, including absent-cost semantics, without merging jobs. Reader recreation and a verified path change preserve valid aggregates.
- [x] Already accepted physical records do not change aggregates or accepted counts on replay, including after bounded-memory eviction. Cover the independently observed cost `0.75` → `1.50` and reverted-session defect within the supported identity contract; identify logical replay at a new physical location when equivalence cannot be proven.
- [x] Fault injection, stale cursor and storage failures demonstrate rollback of record/event, aggregate and cursor updates, no uncommitted UI projection and correct restart recovery.
- [x] Exercise same-identity rename, verified replacement, truncation, weak identity and ambiguous continuity; follow B1 fail-closed rules, retain valid prior aggregates and report unresolved cases. Physical records at different locations remain distinct; logical events with distinct source IDs remain distinct even when their content is identical.
- [x] Preserve Start/Stop, timeline ordering and partition behavior, 500-event UI retention, read-only monitored files, path/security checks and dashboard behavior; run applicable focused and regression checks.

**Exclusions and B3 seam.** B2 does not deliver durable `.log`/`.jsonl` output or claim to fix every duplicate-log symptom in the historical replay reproduction. Define only the safe accepted-event and ordering seam needed by B3; B3 owns durable external-log delivery. No history browsing, search, export or Phase 3 replay UI.

### 1.12 B3 — Durable Logging Outbox

**Status: NOT STARTED. Goal:** Recoverably deliver accepted monitoring events to existing text and JSONL logs through a durable outbox or another architecture approved at the later architecture gate. **Dependencies:** accepted B2 and preservation of B1 guarantees. **Scope:** delivery intent, recovery and reconciliation for existing per-run logs; exclude history browsing, search, export and Phase 3 replay UI.

SQLite transactions cannot atomically commit arbitrary writes to separate external files. The later architecture gate must decide durable intent, event/run identity and ordering, delivery checkpoints, recovery and partial-output reconciliation, including duplicate prevention or detection and correspondence between formats. Exactly-once file delivery is not promised without an implementable, verified mechanism. Establish how persistence and recovery file operations comply with `SEC-011`, create-new and preservation requirements; no additional file location or modification permission is assumed.

**Acceptance criteria:**

- [ ] Durable intent exists for each accepted event within the approved transaction boundary; crashes or restarts before, during and after each output write are recoverable without silent loss.
- [ ] Partial writes, one-format-only writes and interrupted reconciliation are detected and safely reconciled; duplicate prevention or detection is demonstrated within explicit guarantees. Both outputs represent the same accepted events in stable order, with discrepancies visible until recovered.
- [ ] Preserve existing logs and per-Start unique create-new behavior; do not modify unauthorized files. Keep safe summaries and existing privacy and path-security rules.
- [ ] Write and storage failures surface as nonfatal warnings while monitoring continues; pending intent remains recoverable. Recovery work per operation and memory are bounded, with backlog handled without silent loss. Test restart, repeated recovery, ordering, failures and both formats.

### 1.13 B4 — Final Phase 1 Acceptance

**Status: NOT STARTED. Goal:** Validate and sign off integrated Phase 1 after B2 and B3 acceptance. This internal Phase 1 milestone is not application Phase 4 Production Hardening. **Dependencies:** B1, B2 and B3 accepted; outstanding requirement conflicts resolved or explicitly brought to the authorized decision-maker without silently weakening them.

**Acceptance criteria:**

- [ ] Verify existing and newly discovered Forge jobs, dashboard behavior, monitoring lifecycle and repeated Start/Stop, read-only monitored files, and correct cost/session projections across reader recreation, restart and supported replay.
- [ ] Verify timeline partition invariance and ordering, the 500-event UI bound, complete accepted-event logging in both formats, large batches and bounded memory, source continuity and explicit failure states, and file-path security.
- [ ] Run the full regression suite and solution build on the integrated result; obtain independent native QA, review remaining risks and identity/security limits, and record explicit Phase 1 sign-off only when acceptance is supported.

Reuse historical B1 and unchanged-check evidence where applicable; repeat checks when integration changes assumptions or contradictory evidence warrants it. Distinguish worker-reported from independently verified results. The known Codex VSTest/testhost localhost `10013` failure is infrastructure-blocked, neither an application failure nor a pass; existing user-executed non-admin PowerShell results remain valid external evidence. Record missing required independent evidence rather than claiming acceptance.

---

### 1.14 V3-1 — Fresh SQLite Foundation

**Status: ACCEPTED — final independent acceptance, 2026-11-10.** The focused review of the completed V3-1 foundation and final REPLACE ownership correction found no remaining mandatory acceptance blocker. This accepts the fresh V3 schema foundation only. Phase 1 remains **IN PROGRESS / NOT ACCEPTED**; V3-2 remains **NOT STARTED**; B3/B4 and their existing gates are unchanged.

**Final acceptance evidence:**

- Independently inspected the actual `Persistence/EventLedger.cs` ownership-guard diff and additive regression tests. Replacement inserts validate the resulting row, identity-changing updates validate destination ownership, and verified reviewer ownership is rechecked on parent mutation and promotion. Existing test assertions were not removed, disabled, or weakened.
- Read the user's latest standard MSTest artifact, `tests/DSMonitor.Tests/TestResults/v3-final-replace.trx`: **266 passed / 0 failed / 0 skipped**, completed on 2026-11-10 at 05:28:14 +04:00. All 266 individual results passed, including **68 V3 cases**, all **10 new parent-mutation cases**, and every test result from the earlier 256-case run. The earlier 256-pass artifact is not final regression evidence. The latest run also includes 43 existing ledger, 21 B2 projection, and 2 crash-recovery cases.
- Independently invoked **28 focused existing cases: 28 passed / 0 failed / 0 inconclusive**. These cover ownership replacement, reviewer replacement/promotion, harmless conflict actions, exact manifest inventory, reopening, version rejection, transactional ingest rollback, decimal cost, and preservation of B1/B2 databases and sidecars. These are supplemental in-process results, not another standard MSTest run.
- Independently executed **86 targeted synthetic SQL probes: 86 passed / 0 failed** using the existing production SQLite provider with foreign keys enabled and recursive triggers disabled. The matrix covers `INSERT OR REPLACE`, `REPLACE INTO`, `UPDATE`, `UPDATE OR REPLACE`, and UPSERT for both jobs and tasks; incompatible and compatible ownership; unknown job ownership and nonexistent projects; historical and secondary associations; statement rejection and explicit transaction rollback; and discarded conflicting UPSERT inputs.
- Probes started from a real synthetic V3 ingest and compared persisted physical generations, cursors, records/events, logical identities/keys, aggregates, coverage and source associations across mutations and rollback. Dependent run identity and foreign-key integrity remained intact; reopening retained the accepted logical event and exact cost.
- All **11 new or modified triggers** were independently tested both missing and replaced by a same-name no-op (**22 manifest probes**). Reopening rejected every alteration and preserved the database; restoring the exact manifest allowed reopening. Source review confirmed manifest validation also occurs inside the write transaction.
- Tracked-file fingerprints remained unchanged throughout QA before this acceptance-only documentation edit. No production/test changes, restores, installations, delegation, real user database access, V3-2 work, or commit were performed. A redundant full-suite execution was avoided because valid post-fix standard-runner evidence was available.

**Preserved limits.** The accepted B1/B2 identity, recovery, path-security and bounded-read contracts remain supported by the unchanged shared schema and current regression evidence. Producer ID stability, arbitrary semantic exactly-once processing, undetected source rewrites, race-free pathname confinement, power-loss durability and crash-safe external-log delivery remain outside the established guarantees. V3-2 integration and B3/B4 acceptance are not implied.

## Phase 2 - OpenCode Live Monitoring

**Status: NOT STARTED**

**Goal:** Add read-only observation of the existing OpenCode 2.2.6 service and correlate it with forge jobs, without controlling the worker.

**Dependencies:** Phase 1 accepted for the broader Phase 2 scope; installed OpenCode service/OpenAPI schema and confirmed localhost endpoint for any REST use; no changes to external configuration. The authorized job-scoped P0 slice in V3-2–V3-4 may precede Phase 1 acceptance, subject to verified source and message-level attribution.

### 2.1 Tasks

- [ ] Inspect actual OpenAPI schema exposed by installed OpenCode 2.2.6. `[OC-001..004]`
- [ ] Implement safe existing-service discovery without launching a replacement. `[OC-001, OC-004]`
- [ ] Implement read-only GET client with cancellation and unavailable/stale handling. `[ARC-009, OC-003, OC-008, ERR-012..015]`
- [ ] Correlate forge jobs and OpenCode sessions with explicit relationships. `[FR-020, OC-005, LIFE-009..012]`
- [ ] Add recent messages and tool activity from actual evidence. `[OC-006..007, UI-013]`
- [ ] Add running/completed/error tool states. `[OC-007]`
- [ ] Add permission/form/blocker visibility without responding. `[OC-006, SEC-003]`
- [ ] Add files and test activity only where actual evidence exists. `[OC-006, UI-013]`
- [ ] Handle unavailable/restarted service and stale session state. `[OC-008, ERR-012..015]`
- [ ] Add tests with mocks/fixtures and run focused/full regression + build. `[TEST-018..020]`

### 2.2 Acceptance criteria

- [ ] Only verified read-only endpoints are used; no unclear V1 API assumptions.
- [ ] No hardcoded unverified port.
- [ ] No prompt, permission response, configuration change, or session control.
- [ ] Forge monitoring continues when OpenCode is disconnected.
- [ ] Session/tool/test/file/permission information is grounded in actual responses.
- [ ] Multiple jobs sharing a session remain distinct.
- [ ] Automated tests and solution build pass.
- [ ] Independent QA accepts Phase 2.

### 2.3 Validation requirements

- Inspect and record actual endpoint/schema evidence.
- Run focused OpenCode client/correlation tests with mocks.
- Run forge regression tests to preserve Phase 1 behavior.
- Build the solution and run the full relevant suite.
- Verify no external configuration or monitored project files changed.

---

## Phase 3 — History and Diagnostics

**Status: NOT STARTED**

**Goal:** Turn per-run logs into browsable, replayable monitoring history with search, filters, and safe export.

**Dependencies:** Phase 2 accepted; existing `.log`/`.jsonl` files and schema from Phase 1.

### 3.1 Tasks

- [ ] Finalize `.log`/`.jsonl` schema and compatibility. `[LOG-001..013]`
- [ ] Add history browsing by date/run. `[LOG-012, FR-006]`
- [ ] Implement event replay from stored JSONL. `[LOG-012]`
- [ ] Add search and filtering by status, job, project, session, and time. `[UI-012, LOG-012]`
- [ ] Allow opening stored monitoring runs. `[LOG-012]`
- [ ] Add safe copy/export of visible diagnostic summaries. `[SEC-009..013]`
- [ ] Ensure no previous logs are overwritten or deleted. `[LOG-007..008]`
- [ ] Add persistence/replay/retention tests. `[TEST-018]`
- [ ] Run full regression and build. `[ACC-P1-012 pattern]`

### 3.2 Acceptance criteria

- [ ] Stored runs are discoverable after restart.
- [ ] Replay reproduces actual stored events without duplication.
- [ ] Search/filter/export operate on safe summaries.
- [ ] No existing logs are overwritten.
- [ ] Logging/history failures do not break live monitoring.
- [ ] Tests/build pass and independent QA accepts Phase 3.

### 3.3 Validation requirements

- Round-trip serialization/replay fixtures.
- Log naming/retention tests.
- Full Phase 1/2 regression.
- Build validation and manual history browsing check.

---

## Phase 4 — Production Hardening and Polish

**Status: NOT STARTED**

**Goal:** Harden reliability, multi-session behavior, performance, settings, privacy, accessibility, and release quality.

**Dependencies:** Phase 3 accepted; stable schema and APIs.

### 4.1 Tasks

- [ ] Improve multiple-job monitoring and performance. `[PERF-001..010]`
- [ ] Add robust reconnect/recovery behavior. `[ERR-010..021]`
- [ ] Add settings for paths, refresh interval, and log location. `[UI-010]`
- [ ] Add optional cost/cache summaries only when grounded in existing data. `[UI-011]`
- [ ] Add configurable safe log retention without automatic deletion by default. `[LOG-013]`
- [ ] Improve responsive layout, empty/error states, and accessibility. `[UI-001..020]`
- [ ] Run full regression and build validation. `[TEST-020]`
- [ ] Review for unintended file modifications. `[SEC-001..015]`
- [ ] Document installation, usage, limitations, and troubleshooting. `[PRJ-005]`

### 4.2 Acceptance criteria

- [ ] Long-running multi-job monitoring remains responsive and bounded.
- [ ] Recovery from unavailable/restarted services is safe.
- [ ] Settings and retention behave explicitly and never delete by default.
- [ ] Costs/cache metrics are grounded or absent.
- [ ] UI/accessibility/DPI checks pass.
- [ ] Full regression/build passes and independent QA accepts Phase 4.

### 4.3 Validation requirements

- Stress/performance checks with large/bounded fixtures.
- Recovery/cancellation tests.
- Full Phase 1–4 regression.
- Visual/accessibility QA and final file-modification review.

---

## Roadmap Traceability

| Phase | Requirements | Depends on | Status |
|---|---|---|---|
| 1 | `PRJ-001..005`, `ARC-001..008`, `FR-001..046`, `UI-001..020`, `DATA-001..020`, `LIFE-001..015`, `LOG-001..011`, `SEC-001..015`, `ERR-001..011/014..021`, `PERF-001..010`, `TEST-001..017/019..021`, `LEDGER-001..020`, `ACC-P1-001..015` | Existing solution, forge runs | IN PROGRESS / NOT ACCEPTED (B1 and B2 ACCEPTED; B3 and B4 NOT STARTED) |
| 2 | `ARC-009`, `OC-001..010`, `FR-020`, `UI-013..014`, `ERR-012..015`, `TEST-018` | Phase 1 accepted, OpenCode service | NOT STARTED |
| 3 | `LOG-012`, `FR-006`, `UI-012`, `TEST-018`, export/privacy requirements | Phase 2 accepted, stored logs | NOT STARTED |
| 4 | `PRJ-005`, `PRJ-008`, `FR-045`, `UI-019`, `LOG-013`, `PERF-001..010`, `SEC-009..015`, `TEST-020` | Phase 3 accepted | NOT STARTED |

## Change Log

- **2026-11-12 — V1 Live Activity selection and complete-text view:** Added per-job activity selection to the existing three-column history grid. Newest is selected by default, the exact job/source/session/message/part identity survives refreshes (including a newer physical record for the same part), and selection falls back to the newest row only when that identity disappears. Job and activity selections remain independent per job. The Live Activity tab now has exactly one `Observed Activity` heading outside a compact read-only multiline selectable/scrolling TextBox showing the selected activity’s complete guarded A1 source text, followed immediately by the visibly usable `Activity History` grid (`Started | Duration | Reported Status`). The old Task/Doing/Done/status-card/provenance/technical paragraphs were removed. The viewer does not apply the 4,000-character history-preview cap; unavailable or incomplete guarded retrieval shows a short neutral indication and is never labeled complete. The Forge jobs table cell/tooltip and the corresponding Activity History row share one normalized first-line status: an initial `##Status`/`##Result` marker with optional whitespace/colon and a leading `Status:` label are removed, a leading `**...**` span ends at its closing marker with internal punctuation preserved, a plain first line ends at its first sentence period, and a missing closing marker is preserved safely. An empty first line falls back to the existing owned reported value without mixing jobs; the general A2 parser and raw selected-text viewer are unchanged. The shared spinner uses active monitoring plus job-scoped activity/lifecycle evidence, includes a running job with no Doing record and a later step-start after a terminal event, and stops on a job-scoped terminal step-finish at or after the latest activity by physical source offset (preventing out-of-order terminal resurrection) or when monitoring stops. No Forge completion is inferred from text and no A1/A2, ledger, ingestion, cost, or left-table behavior was changed. New/adjusted synthetic coverage: selection default/refresh/fallback and job isolation, history heading variants and fallbacks, untruncated complete multiline text and short incomplete text, guarded retrieval, spinner terminal/stop behavior, and rendered UI surface/bindings. Focused 176/176 and broader synthetic 501/501 passed with 0 skipped (`RealForgeDataSmokeTests` excluded), isolated no-restore build 0 warnings/0 errors. Existing tests were preserved; the prior rendered Task/Doing/Done/provenance assertions were replaced because those controls were explicitly removed, not weakened. Noncritical limits are recorded in `LAH-BETA-011..013`. Phase 1 remains IN PROGRESS / NOT ACCEPTED; acceptance status was not changed.

- **2026-11-12 — V1 Live Activity minimal stabilization:** Fixed six accepted V1 regressions without adding a reader, storage, lifecycle engine, or extra source read. (1) History now requires every previously retained record inside the new bounded window to reappear at the same offset/identity/digest, not merely one matching later record. (2) The guarded reader exposes pre-dedup exact-offset records so a legitimate same-part update no longer hides the overlap proof and drops older rows. (3) Whole-scan failures publish neutral unavailable activity and clear transient history/status instead of republishing stale rows; a committed aggregate/coverage read failure clears the omitted job's buffers. (4) The spinner is suppressed for exact same-part step-finish/tool-end evidence and whenever monitoring stops, fails, or is inactive. (5) Control-to-space sanitization now consumes the same 4,000-character history budget. (6) Negative and non-representable Unix-millisecond tool timestamps yield `N/A` instead of a false zero duration. Synthetic reproductions: 8/8 failed before the fix; after the fix 22/22 focused Live Activity tests and 460/460 broader synthetic tests passed (0 skipped, `RealForgeDataSmokeTests` excluded by filter), with a no-restore build at 0 warnings/0 errors (isolated output because the user's running app locked the default output). No existing test was deleted, skipped, or weakened. Remaining bounded-history limitations stay recorded in `LAH-BETA-001..010`; no unsafe behavior is deferred. Phase 1 remains IN PROGRESS / NOT ACCEPTED; acceptance status was not changed.

- **2026-11-12 — V1 Live Activity history tab:** Added a default `Live Activity` tab immediately before `Overview` and moved the existing task/Doing/Done/provenance content there. The tab adds exactly `Started | Duration | Reported Status`, newest source timestamp first (unknown timestamps last, byte-offset tie-break), complete bounded text and tooltips, WPF available-width ellipsis, neutral compact styling, and the shared spinner only on the exact current Doing target when Forge metadata explicitly reports RUNNING. History accumulates per exact ordinal job/session/message/part identity from the existing guarded parse loop (no extra source reads), uses the newest physical record without duplicate rows, and is carried across refreshes only with verified job/path/physical identity/generation, nondecreasing length, and exact overlapping record evidence. It clears on missing proof, source conflict/unavailability, truncation/replacement, omitted jobs, whole-scan fallback, and restart. Per-entry duration uses only a valid same-part tool `state.time` start/end pair; otherwise `N/A`. Reported Status uses an exact-bound unambiguous current target, otherwise the row's own text. New synthetic regressions cover reader history, accumulation/dedup/order/equal-time, continuity and clearing, job isolation, refresh/update/delete service behavior, status/timing/spinner binding, and tab/column layout. Noncritical bounded-history limitations are recorded in `LAH-BETA-001..010`. Phase 1 remains IN PROGRESS / NOT ACCEPTED; acceptance status was not changed.
- **2026-11-12 — V1 compact table and live activity presentation:** The Jobs table keeps `Project | Started | Duration | Job | Reported Status` with a compact fixed Project column (~12 characters), final-folder text, WPF ellipsis, full-path tooltip, and reduced padding/row height. Reported Status keeps the complete agent-reported value (the fixed 15-character truncation was removed), uses WPF ellipsis plus a full unverified tooltip, and falls back to `• ` plus the job's own complete Doing excerpt when no unambiguous reported status exists. Active rows show a small light-green spinner before the cell text; it is removed when the row is no longer active. The shared one-second dispatcher timer now advances only RUNNING/BLOCKED Duration values without log reads. Terminal metadata freezes at the best observed timestamp; metadata completion is labeled reported; an UNKNOWN job uses a Done-activity heuristic only when the done record belongs to the job and no later activity exists, otherwise the observed span is used. Heuristic and reported bases are tracked internally. Synthetic regressions cover ticking/frozen behavior, UNKNOWN observed-span fallback, correct-job/conflicting Done records, full reported/status-fallback text, tooltip completeness, and spinner visibility binding. Remaining noncritical limitations are recorded above with the Beta Stabilization milestone. Phase 1 remains IN PROGRESS / NOT ACCEPTED; acceptance status was not changed.
- **2026-11-09 — cost-bearing replay acceptance remediation:** Added an optional acceptance predicate to `ForgeLogTailReader.Poll`, evaluated after reader-local dedupe but before cost, session, activity, or accepted-unique-count mutation. `ForgeMonitoringService` now supplies retained/recent/per-poll acceptance, so a rejected duplicate has no projected cost or session effect; legitimate events still update once, and both persistent logs retain exactly the accepted events. Added deterministic service and reader regressions for immediate duplicates, >500 retention, >10,000-key recent-window expiry, session continuation, incremental/distinct identities, repeated polling, and replacement. Pre-fix regression doubled cost from `0.750` to `1.500`; post-fix cost, session, accepted count, retained timeline, and both logs are unchanged. Build 0/0, full suite 114 passed / 0 skipped / 114 total, targeted cost 5/5, timeline/oracle+cost 36/36, integration/security/logging 47/47. Exact idempotency outside the bounded recent/retained windows remains unavailable and unclaimed. Phase 1 remains IN PROGRESS / NOT ACCEPTED pending independent Sol QA; Phases 2–4 remain NOT STARTED.
- **2026-11-09 — timeline remediation readiness:** The authorized follow-up could not start: non-generative OpenCode agent and authentication checks were denied access to `opencode.log` in the Codex sandbox. The bounded agent file specifies 120 steps, but effective agent selection and authentication could not be verified. No worker, implementation, tests, or independent QA ran in this follow-up. Phase 1 remains IN PROGRESS / NOT ACCEPTED; the successful manual file-symlink validation remains valid.
- **2026-11-09 — manual symlink validation:** User reported the real file-symlink integration test passed separately in a normal non-administrator Windows session after enabling Developer Mode: 1 passed, 0 failed, 0 skipped. Timeline partition invariance remains unresolved; Phase 1 is not accepted.
- **2026-11-09:** One authorized bounded remediation completed. Independent build clean; 82 tests passed / 1 skipped. All 800 events independently verified in both logs; junction guards verified. Independent Sol review returned CHANGES REQUIRED for newest-timestamp retention and mixed missing/known timestamp order; file-symlink coverage remains blocked by missing Windows privilege. Phase 1 not accepted; Phases 2–4 NOT STARTED.
- **2026-11-09 — bounded ordering/symlink follow-up:** Added shared `TimelineOrdering` source-order barrier policy, service-retention integration, and service→VM ordering/retention tests. File-symlink fixture P/Invoke now uses `ExactSpelling`, `U1` return marshaling, and explicit Win32 error reporting; real fixture still unavailable (1314). Phase 1 remains IN PROGRESS pending independent QA and manual symlink verification.
- **2026-11-09 — final correction 2/2:** Persisted a bounded per-job “has observed ambiguous timestamps” mode in the monitoring run and `ForgeJobSnapshot`; source-window retention no longer switches back to all-known mode after barrier eviction. Added the 502-event single/501+1/1+501/250+252/500+2 service→VM regression and `.log`/`.jsonl` 502/502 persistence checks. Remaining correction allowance: zero. Phase 1 remains IN PROGRESS pending independent final QA.
- **2026-11-09 — bounded dual-window retention correction:** Added `TimelineRetentionBuffer`, which keeps both the last 500 source events and the 500 newest timestamped events per job so a late missing timestamp cannot reveal that needed source candidates were already trimmed. The service publishes the policy-selected bounded window; the monotonic ambiguity flag and shared display ordering remain. Added an independent full-history oracle and partition/shape/reader/service→VM coverage. Local results: build 0/0; oracle 5/5; targeted timeline/oracle 32/32; full suite 107 passed / 0 skipped / 107 total. No existing test modified. Phase 1 remains IN PROGRESS / NOT ACCEPTED pending independent Sol QA; no correction consumed.
- **2026-11-09 — sole Sol QA correction consumed:** Independent Sol QA identified a long-history replay duplicate. Added retained-key dedupe (`TimelineRetentionBuffer.ContainsRetainedKey`) so a replayed high-timestamp line whose bounded recent key was evicted but whose event remains in a retention window is rejected. `ParsedEventCount` now tracks accepted unique events, so rejected replay does not inflate the count. The oracle suite also asserts visible order at every intermediate prefix and now reports 229 valid scenario/partition combinations. Added an additive reader regression confirming the bounded 10,000-key window re-emits the line so the service-level guard is exercised. Correction evidence: pre-fix service replay regression expected 1 snapshot entry but saw 2; post-fix snapshot/view-model/JSONL each contain the high-timestamp event once. Build 0/0; oracle 6/6; targeted timeline/oracle 33/33; integration/security 37/37; full suite 109 passed / 0 skipped / 109 total. Zero correction attempts remain; Phase 1 remains IN PROGRESS / NOT ACCEPTED.

- **2026-11-08:** Reorganized requirements and roadmap; implemented Phase 1 and passed 65 tests/build. Independent Sol review returned CHANGES REQUIRED (path confinement, event loss, timeline order); running-app visual check passed. Phase 1 remains IN PROGRESS and Phases 2–4 remain NOT STARTED.

## Historical Independent Follow-up Verdict — 2026-11-09

The findings and status labels in this historical record describe earlier reviews. The later B2 acceptance in section 1.11 supersedes the B2-related findings; final Phase 1 acceptance remains pending.

**CHANGES REQUIRED. Phase 1 remains IN PROGRESS / NOT ACCEPTED.** This verdict supersedes earlier pending-review labels and implementation claims above.

- Independent timeline filters: **27 passed / 0 failed / 0 skipped**.
- Independent security/junction/parser/tailer filters: **34 passed / 0 failed / 1 skipped**.
- Full suite: `dotnet test Tests/DSMonitor.Tests/DSMonitor.Tests.csproj --no-build` — **101 passed / 0 failed / 1 skipped / 102 total**.
- Final independent `dotnet build DSMonitor.slnx --no-restore` — **0 warnings / 0 errors**; worker exact solution build also passed.
- Original assertions preserved; test changes were additive or fixture/helper adaptations. Production guarded-file code and previously fixed tail logging were unchanged.
- All 53 source/document/test fingerprints and file inventory captured by the parent remained unchanged during final review. The reviewer separately verified all 12 affected files. Documentation was updated after review to record this verdict.

**Resolved timeline cases:** all-known newest-500 retention, missing-first ordering, equal timestamps, and the earlier missing-first 502-event single/split reproduction. The persistent per-job ambiguity flag is bounded and resets with a new monitoring run. All accepted events still reach the monitoring logs independently of display retention; 800-event and 502-event integration checks passed.

**Remaining P2 — First missing timestamp arrives after retention:** actual service → view-model reproduction used the same 502-event source `[known502, known501, ..., known2, missing]`. A single poll retained `known500..2 + missing`; split `501 + 1` retained `known501..3 + missing`. Both parsed 502 events and displayed 500. Timestamp-based retention had already discarded source arrivals needed by the later source-window policy. A persistent ambiguity flag cannot recover those discarded candidates. No further repair was performed. A possible future bounded repair is to retain both a bounded last-500 source window and newest-500 timestamp candidate set until ambiguity is observed, then consistently use the source window; that design is a proposal, not implemented or verified. **Addendum 2026-11-09:** that bounded dual-window design is now implemented and locally oracle-verified as recorded in section 1.7; independent Sol QA acceptance is still pending.

**Real file-symlink gate:** **VERIFIED / PASSED** subsequently, based on the user-reported manual run described in section 1.6. At the time of this independent review, attempts with flags 2 and 0 returned Win32 1314 and the integration test was skipped. The separate later manual test passed with a real link; junction and simulated checks alone did not establish that coverage.

**Bounded task closed:** both local correction attempts consumed (STA harness, persistent ambiguity mode). Zero remaining; original repair history remains intact. One Sol reviewer performed the consolidated review and focused final revalidation. Phases 2–4 remain **NOT STARTED**.

**Latest timeline follow-up — independent Sol revalidation: CHANGES REQUIRED.** The separately authorized task used its sole focused correction; zero corrections remain. Sol confirmed the retained-key guard and every-prefix display assertions by source review, and independently passed the serial `dotnet build DSMonitor.slnx --no-restore --maxcpucount:1` (0 warnings/errors) and `git diff --check`. Independent tests could not start because testhost's localhost connection was denied with SocketException 10013; worker-reported 109/109 full, 33/33 timeline, and 37/37 integration/security results remain worker evidence. A source-derived unresolved case remains: replaying a retained cost-bearing event after its recent dedupe key expires can increment reader cost and update session/activity before the service rejects the duplicate; the projected cost can therefore be inflated. This was not independently runtime reproduced. Phase 1 remains IN PROGRESS / NOT ACCEPTED; Phases 2–4 remain NOT STARTED. No further repair is authorized in this task.

**Cost-bearing replay follow-up — 2026-11-09 (new separately authorized bounded task): INDEPENDENT SOL QA — CHANGES REQUIRED.** The retained-event case above is addressed locally by evaluating retained/recent acceptance before reader aggregate mutations; Sol independently reproduced that case successfully. Sol also reproduced cost and session corruption for a fully evicted replay and loss of cost/session on a changed log path. See section 1.8 for evidence and the unresolved bounded-identity limitation. Phase 1 remains IN PROGRESS / NOT ACCEPTED; Phases 2–4 remain NOT STARTED.

## Bounded Dual-Window Retention Addendum — 2026-11-09

**Status: IMPLEMENTED / PENDING INDEPENDENT SOL QA (SOLE CORRECTION CONSUMED). Phase 1 remains IN PROGRESS / NOT ACCEPTED.**

The previously remaining P2 defect (late first missing timestamp after all-known retention) is addressed by the implementation documented in section 1.7. `TimelineRetentionBuffer` retains both the last 500 source events and the 500 newest timestamped events per job, so the documented source-window mode can be selected at snapshot time without any partition having already discarded a required candidate. The monotonic ambiguity flag is retained for view-model mode selection but is not the recovery mechanism.

Local verification after the change: build 0 warnings / 0 errors; independent oracle suite 6/6; targeted timeline/oracle suite 33/33; integration/security 37/37; full suite 109 passed / 0 skipped / 109 total. The 502-event `[known502, ..., known2, missing]` source now yields identical retained IDs and visible order for single, 501+1, 1+501, 500+2, 250+252, uneven, and deterministic randomized partitions, including intermediate prefixes. A replayed high-timestamp line after more than 10,000 distinct keys remains a single retained event in the snapshot, view model, and JSONL. All accepted events remain in both persistent logs. Existing assertions were preserved; one additive reader regression was added.

This addendum records implementation evidence only; it is not independent QA acceptance. The sole authorized correction for that task was consumed by the retained-key replay dedupe and the every-prefix display assertion; zero correction attempts remain for it. The subsequent cost-bearing replay remedy is recorded in section 1.8 as a new separately authorized initial implementation and does not reuse that exhausted allowance. Phases 2–4 remain **NOT STARTED**.
- **2026-11-09 — B1 durable SQLite event-ledger foundation:** Added `Persistence/EventLedger.cs` and `Persistence/EventLedgerModels.cs` as a separate byte-oriented persistence seam; added `Microsoft.Data.Sqlite` 10.0.14; added isolated deterministic tests and documented `LEDGER-001..020`/`SEC-011`/`TEST-021`. Schema version 1 uses verified physical source generations, complete `[start,end)` records, explicit dispositions, safe event metadata, and an atomically guarded committed cursor. Existing live monitoring, timeline, logging, and security behavior are unchanged. The architecture gate, historical findings, and user-verified real file-symlink result are preserved. Independent Sol revalidation of B1 is pending; Phase 1 remains IN PROGRESS / NOT ACCEPTED and Phases 2–4 remain NOT STARTED.
- **2026-11-09 — B1 post-QA correction (sole authorized):** Added exact schema-object/DDL validation and unexpected trigger/view/index rejection; verified expected insert/update affected rows and rollback on silent suppression; added `Persistence/LedgerStorageGuard.cs` for reparse-point and final-path checks on the database and WAL/SHM sidecars with documented TOCTOU residual; added bounded keyset pagination to `ListRecords`/`ListEvents`; added a real literal UTF-8 multibyte byte sequence split across polls. Focused suite 31/31, timeline 33/33, logging/integration/security 47/47, full suite 145 passed / 0 skipped / 145 total, build 0 warnings/0 errors, `git diff --check` clean. No existing tests weakened; independent Sol revalidation pending; the sole post-QA correction is consumed.
