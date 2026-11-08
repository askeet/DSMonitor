# DSMonitor — Executable Development Roadmap

**Document version:** 2.0  
**Date:** 2026-11-09
**Source of truth:** `docs/Project Requirements.md` (stable requirement IDs) and this roadmap.  
**Current phase:** Phase 1 IN PROGRESS; independent Sol remediation review returned CHANGES REQUIRED. Event loss is resolved; timeline defects and file-symlink validation remain open. Prior running-dashboard visual checks passed; no interactive checks were performed during remediation.
**Future phases:** Phase 2–4 are **NOT STARTED**.

> Status vocabulary: `NOT STARTED / IN PROGRESS / COMPLETED / BLOCKED`.
> A task is checked only when implementation and the required validation actually succeed.

---

## Phase 1 — WPF Dashboard and Forge Monitoring

**Status: IN PROGRESS** — independent build passed and full tests returned 82 passed / 1 skipped. Independent remediation review found two timeline defects and incomplete file-symlink coverage. Phase 1 is not accepted.

**Goal:** Deliver a polished, read-only WPF dashboard that starts and stops DSMonitor monitoring, discovers existing/new forge-delegate jobs, incrementally reads metadata and logs, shows real events/statuses safely, and writes one unique monitoring log per Start.

**Dependencies:** existing DSMonitor .NET 10 WPF solution; forge-delegate 1.2.4 runs directory; .NET 10 SDK and MSTest package restore; no OpenCode REST dependency.

### 1.1 Tasks

- [x] Review `docs/Project Requirements.md`, `docs/Plan.md`, `docs/FormView.png`, and the existing WPF project.
- [x] Inspect installed forge-delegate 1.2.4 metadata and JSONL formats before designing parsers.
- [x] Preserve the original source/docs baseline outside the live solution and record a baseline build.
- [x] Implement MVVM dashboard shell with dark theme and reusable styles. `[UI-001..020, ARC-001, ARC-006]`
- [x] Implement STOPPED startup and Start/Stop lifecycle. `[FR-001..005, FR-030..035, LIFE-014]`
- [x] Implement forge runs discovery for existing and newly created jobs. `[FR-010..019, DATA-001..008]`
- [ ] Complete defensive path validation coverage: guarded opens and junction tests independently verified; file-symlink test remains skipped due to Windows privilege restriction. `[DATA-002..008, SEC-012]`
- [x] Resolve large-batch event loss: independent reproduction verified all 800 events persisted to both logs with UI capped at 500. `[DATA-010..020, PERF-001..005]`
- [ ] Complete timeline ordering: newest-event retention and mixed missing/known timestamp ordering remain defective after independent re-review. `[UI-015, DATA-015, PERF-007]`
- [x] Implement event dedupe and bounded timelines. `[DATA-015, DATA-017, TEST-006]`
- [x] Implement explicit forge status mapping, UNKNOWN default, and separate monitoring state. `[LIFE-001..013]`
- [x] Implement job/session correlation without merging jobs. `[LIFE-009..013, FR-020]`
- [x] Implement IDLE with visible UNKNOWN count when no active job is confirmed. `[FR-013, LIFE-007..008]`
- [x] Implement per-run unique `.log` and `.jsonl` logging with safe summaries and graceful failures. `[LOG-001..011, SEC-011]`
- [x] Implement job table, details tabs, real timeline, bounded raw-log preview, status bar, and empty/error states. `[UI-010..020, FR-040..046]`
- [ ] Complete security validation: guarded metadata/tail/preview opens independently reviewed with junction coverage passing; file-symlink behavior remains unverified. `[SEC-001..015]`
- [x] Add meaningful automated tests for parsing, tailing, lifecycle, status, logs, cancellation, paths, and UI projection. `[TEST-001..017, TEST-019..020]`
- [x] Run focused tests, full relevant test suite, and solution build. `[ACC-P1-012]`
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
| Testing | `TEST-001..017`, `TEST-019..020` |
| Acceptance | `ACC-P1-001..015` |

### 1.3 Acceptance criteria

- [x] Existing solution builds on .NET 10 with zero warnings/errors.
- [x] MainWindow loads, lays out, and renders empty/populated states without XAML exceptions.
- [x] Start Monitoring creates a unique run log and begins forge observation.
- [x] No confirmed active job results in IDLE with UNKNOWN count visible.
- [x] Existing jobs are displayed; missing status is UNKNOWN, not guessed.
- [x] New jobs are detected without restarting DSMonitor.
- [x] Selecting a job displays actual available metadata/events.
- [x] Incremental reads preserve all accepted events: independent reproduction persisted 800/800 in both JSONL and text logs with bounded UI retention. Correct selection of the newest retained events remains open separately.
- [x] Stop Monitoring stops only DSMonitor and closes its log files.
- [x] Repeated Start/Stop cycles create unique logs and clean up watchers/writers.
- [x] Forge files are byte-for-byte unchanged by monitoring.
- [x] No HTTP/OpenCode REST/Prompt/permission/process-control code exists in Phase 1.
- [x] All 65 baseline tests and 17 applicable new tests pass; 1 environment-conditional file-symlink fixture is skipped.
- [ ] Independent Sol QA accepted. **CHANGES REQUIRED**
- [x] Independent visual check of running dashboard passed; broader DPI/accessibility validation remains Phase 4.

### 1.4 Validation evidence

| Check | Command | Result |
|---|---|---|
| Baseline build | `dotnet build .\DSMonitor.slnx` | Passed before remediation: 0 warnings, 0 errors |
| Baseline tests | `dotnet test .\Tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build` | Passed before remediation: 65/65 |
| Remediation targeted tests | `dotnet test .\Tests\DSMonitor.Tests\DSMonitor.Tests.csproj --filter "FullyQualifiedName~SafeFileAccessTests|FullyQualifiedName~JobRowTimelineOrderingTests"` | 12 passed, 1 skipped (file-symlink fixture unsupported in this environment) |
| Remediation batch tests | `dotnet test .\Tests\DSMonitor.Tests\DSMonitor.Tests.csproj --filter "FullyQualifiedName~LargeBatch|FullyQualifiedName~SuccessiveLargeBatches"` | 5/5 passed |
| Phase 1 solution build | `dotnet build .\DSMonitor.slnx` | Passed: 0 warnings, 0 errors |
| Full local suite | `dotnet test .\Tests\DSMonitor.Tests\DSMonitor.Tests.csproj --no-build` | Passed: 83 total, 82 passed, 1 skipped (environment-conditional symlink test); all 65 baseline tests preserved |
| UI empty-state smoke | MSTest `MainWindow_LoadsLaysOutAndRendersWithoutException` | Passed in full suite |
| UI populated-state smoke | MSTest `MainWindow_PopulatedStateRendersAndShowsRealDataControls` | Passed in full suite |
| Read-only check | `Run_ReadOnly_DoesNotModifyForgeFiles` | Passed |
| Unique logs / close check | `Run_RepeatedLifecycle_CreatesUniqueLogsAndClosesThem` | Passed |
| Large-batch logging | `Run_LargeBatch_LogsAllEventsWhileSnapshotRetainsNewest500` | Passed: 800 JSONL events, 500 UI snapshot events |
| Path confinement | `OpenRead_JunctionEscape_IsRejectedBeforeContentRead`, `Discovery_RootJunction_FailsClosed`, `Reader_PathReplacedWithJunctionAfterConstruction_FailsClosed` | Passed |
| Timeline ordering | `Update_DistinctTimestampsOutOfOrder_SortsNewestFirst`, equal/missing source-order tests, 500/501/800 retention tests | Passed |

### 1.5 Known limitations / risks

- forge-delegate 1.2.4 metadata has **no status field**. Therefore current real jobs display UNKNOWN by default; RUNNING/TIMED OUT require explicit future metadata/events. This is intentional and safer than PID inference.
- A past forge deadline is shown as wrapper-deadline information, not as worker termination. Historical completed jobs are not relabeled TIMED OUT without explicit evidence.
- Cost is available only when `step_finish.part.cost` appears in logs; otherwise it shows `—`.
- Tool Activity/Tests/Files tabs are Phase 2 placeholders; Phase 1 only has event summaries and a bounded Raw Log preview.
- Saved monitoring history browsing/replay is Phase 3. Phase 1 creates the files but does not provide a history UI.
- Settings is a disabled Phase 4 placeholder.
- Independent Sol QA returned CHANGES REQUIRED. A single authorized bounded remediation task implemented fixes for the three findings, but their acceptance remains pending independent re-review.
- FileSystemWatcher is best-effort; periodic reconciliation is the correctness backstop.

**Remediation evidence for independent review findings (2026-11-09; acceptance pending):**

- **P1 — Path confinement — IMPLEMENTED / PENDING INDEPENDENT QA.** Added `Services/SafeFileAccess.cs` and routed metadata parsing, tail polling, preview reads, and discovery root validation through guarded read-only opens. It rejects reparse points before opening (including root/ancestors), validates the opened handle's final DOS path against the resolved trusted root, checks the actual handle for reparse attributes, preserves `FileShare.ReadWrite | FileShare.Delete`, and fails closed with warnings. Temporary junction tests cover escape rejection, root-junction discovery refusal, and a runs-root replaced by a junction after reader construction. A file-symlink test is environment-conditional and skipped because this environment cannot create a usable file symlink.
- **P2 — Event loss — IMPLEMENTED / PENDING INDEPENDENT QA.** Removed the per-poll 500-event drop from `ForgeLogTailReader`; byte/line bounds remain, dedupe/cursor behavior is preserved, and all accepted events are returned for logging. Tests cover 500/501/800-event batches and successive 300/300/200 appends. End-to-end `Run_LargeBatch_LogsAllEventsWhileSnapshotRetainsNewest500` confirms 800 `log_event` entries in `.jsonl` while the snapshot/UI remains capped at 500.
- **P2 — Timeline order — IMPLEMENTED / PENDING INDEPENDENT QA.** `JobRowViewModel` now uses a stable newest-first timestamp ordering, no `ObservedAt`/`EventKey` tie-break, and preserves original source order for equal/missing timestamps. Tests cover out-of-order timestamps, equal/missing timestamps across batches, and 500/501/800 retention boundaries. The service snapshot now preserves source order rather than re-sorting by key.

**Remediation validation:** Baseline `dotnet build DSMonitor.slnx` passed 0/0; baseline `dotnet test Tests/DSMonitor.Tests/DSMonitor.Tests.csproj --no-build` passed 65/65. Final build passed 0/0. Final full suite: 83 total, 82 passed, 1 skipped (file-symlink fixture unsupported in this environment); all 65 baseline tests preserved. Targeted remediation filters passed 5/5 batch tests and 12/13 path/ordering tests (1 environment skip).

**Repair limit:** original two focused repair cycles were exhausted before this exception. The authorized exception allowed two internal correction attempts; both were used (symlink fixture support handling; outside-root `LogMissing` compatibility). No further correction attempts remain.

### 1.6 Independent remediation review — 2026-11-09

**Verdict: CHANGES REQUIRED.** The one additional user-authorized bounded DeepSeek task is finished; no further implementation was delegated. All 65 original tests still pass, and review found no deleted or weakened original tests. The full suite includes one new skipped symlink test, so the user's no-skipped-tests acceptance condition is unmet.

| Check | Independently observed result |
|---|---|
| `dotnet build DSMonitor.slnx` | Passed; 0 warnings / 0 errors |
| Targeted path, timeline, and batch filters listed above | 17 passed / 1 skipped / 18 total |
| `dotnet test Tests/DSMonitor.Tests/DSMonitor.Tests.csproj --no-build` | 82 passed / 1 skipped / 83 total |
| Successive-batch end-to-end log reproduction | All 800 events in both actual `.jsonl` and `.log`; UI capped at 500 |
| Junction escape, root junction, root replacement | Passed; no concrete remaining path-confinement defect found |
| File-symlink fixture | Unverified: independent Win32 attempts with flags 0 and 2 returned error 1314; .NET likewise reported missing privilege |

**Remaining findings:**

- **P2 — Newest timestamp events lost from retained display:** `Services/ForgeMonitoringService.cs:277` trims by source position before timestamp ordering. Independent descending-timestamp appends at 500 → 501 → 800 events retained the true newest event at 500 but discarded it at 501 and 800. Apply agreed ordering before selecting the retained 500, and cover this through the service-to-view-model path.
- **P2 — Ambiguous mixed timestamp ordering:** `ViewModels/JobRowViewModel.cs:156` substitutes `MinValue` for absent timestamps. Independent `[missing-first, known-second]` input becomes `[known-second, missing-first]`, violating source-order preservation where no reliable comparison exists. Add mixed known/missing timestamp regression coverage across batches.
- **Validation gap — File symlinks:** run the existing new regression in an environment permitted to create file symlinks; a skip does not establish safety or meet the no-skips requirement. No OpenCode, forge, agent, or Windows security settings were changed.

Earlier implementation/pending-review notes in section 1.5 describe worker evidence; this final independent verdict supersedes their pending labels. The event-loss finding is resolved. The path fix is implemented with independently verified junction protection, but broader symlink validation is incomplete. Timeline acceptance remains open.

**Phase completion gate:** repair and independently revalidate both remaining timeline cases; complete file-symlink testing without skips. Phases 2–4 remain NOT STARTED. No interactive computer use or screen access occurred during remediation.

---

## Phase 2 — OpenCode Live Monitoring

**Status: NOT STARTED**

**Goal:** Add read-only observation of the existing OpenCode 2.2.6 service and correlate it with forge jobs, without controlling the worker.

**Dependencies:** Phase 1 accepted; installed OpenCode service/OpenAPI schema; confirmed localhost endpoint; no changes to external configuration.

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
| 1 | `PRJ-001..005`, `ARC-001..008`, `FR-001..046`, `UI-001..020`, `DATA-001..020`, `LIFE-001..015`, `LOG-001..011`, `SEC-001..015`, `ERR-001..011/014..021`, `PERF-001..010`, `TEST-001..017`, `ACC-P1-001..015` | Existing solution, forge runs | IN PROGRESS (CHANGES REQUIRED) |
| 2 | `ARC-009`, `OC-001..010`, `FR-020`, `UI-013..014`, `ERR-012..015`, `TEST-018` | Phase 1 accepted, OpenCode service | NOT STARTED |
| 3 | `LOG-012`, `FR-006`, `UI-012`, `TEST-018`, export/privacy requirements | Phase 2 accepted, stored logs | NOT STARTED |
| 4 | `PRJ-005`, `PRJ-008`, `FR-045`, `UI-019`, `LOG-013`, `PERF-001..010`, `SEC-009..015`, `TEST-020` | Phase 3 accepted | NOT STARTED |

## Change Log

- **2026-11-09:** One authorized bounded remediation completed. Independent build clean; 82 tests passed / 1 skipped. All 800 events independently verified in both logs; junction guards verified. Independent Sol review returned CHANGES REQUIRED for newest-timestamp retention and mixed missing/known timestamp order; file-symlink coverage remains blocked by missing Windows privilege. Phase 1 not accepted; Phases 2–4 NOT STARTED.

- **2026-11-08:** Reorganized requirements and roadmap; implemented Phase 1 and passed 65 tests/build. Independent Sol review returned CHANGES REQUIRED (path confinement, event loss, timeline order); running-app visual check passed. Phase 1 remains IN PROGRESS and Phases 2–4 remain NOT STARTED.
