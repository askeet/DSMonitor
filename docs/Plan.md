# DSMonitor — Executable Development Roadmap

**Document version:** 2.0  
**Date:** 2026-11-08  
**Source of truth:** `docs/Project Requirements.md` (stable requirement IDs) and this roadmap.  
**Current phase:** Phase 1 IN PROGRESS; independent Sol QA returned CHANGES REQUIRED. Running-dashboard visual checks passed.  
**Future phases:** Phase 2–4 are **NOT STARTED**.

> Status vocabulary: `NOT STARTED / IN PROGRESS / COMPLETED / BLOCKED`.
> A task is checked only when implementation and the required validation actually succeed.

---

## Phase 1 — WPF Dashboard and Forge Monitoring

**Status: IN PROGRESS** — implementation, build, and 65 automated tests passed, but independent QA found unresolved defects. Phase 1 is not accepted.

**Goal:** Deliver a polished, read-only WPF dashboard that starts and stops DSMonitor monitoring, discovers existing/new forge-delegate jobs, incrementally reads metadata and logs, shows real events/statuses safely, and writes one unique monitoring log per Start.

**Dependencies:** existing DSMonitor .NET 10 WPF solution; forge-delegate 1.2.4 runs directory; .NET 10 SDK and MSTest package restore; no OpenCode REST dependency.

### 1.1 Tasks

- [x] Review `docs/Project Requirements.md`, `docs/Plan.md`, `docs/FormView.png`, and the existing WPF project.
- [x] Inspect installed forge-delegate 1.2.4 metadata and JSONL formats before designing parsers.
- [x] Preserve the original source/docs baseline outside the live solution and record a baseline build.
- [x] Implement MVVM dashboard shell with dark theme and reusable styles. `[UI-001..020, ARC-001, ARC-006]`
- [x] Implement STOPPED startup and Start/Stop lifecycle. `[FR-001..005, FR-030..035, LIFE-014]`
- [x] Implement forge runs discovery for existing and newly created jobs. `[FR-010..019, DATA-001..008]`
- [ ] Complete defensive path confinement: parser implemented, junction escape remains unresolved. `[DATA-002..008, SEC-012]`
- [ ] Complete incremental, bounded JSONL tail reader: large-batch event loss remains unresolved. `[DATA-010..020, PERF-001..005]`
- [x] Implement event dedupe and bounded timelines. `[DATA-015, DATA-017, TEST-006]`
- [x] Implement explicit forge status mapping, UNKNOWN default, and separate monitoring state. `[LIFE-001..013]`
- [x] Implement job/session correlation without merging jobs. `[LIFE-009..013, FR-020]`
- [x] Implement IDLE with visible UNKNOWN count when no active job is confirmed. `[FR-013, LIFE-007..008]`
- [x] Implement per-run unique `.log` and `.jsonl` logging with safe summaries and graceful failures. `[LOG-001..011, SEC-011]`
- [x] Implement job table, details tabs, real timeline, bounded raw-log preview, status bar, and empty/error states. `[UI-010..020, FR-040..046]`
- [ ] Complete security constraints: forge files remain unchanged, but junction-based outside reads remain possible. `[SEC-001..015]`
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
- [ ] Incremental reads preserve all events: normal cases pass, but batches above 500 events lose entries before logging.
- [x] Stop Monitoring stops only DSMonitor and closes its log files.
- [x] Repeated Start/Stop cycles create unique logs and clean up watchers/writers.
- [x] Forge files are byte-for-byte unchanged by monitoring.
- [x] No HTTP/OpenCode REST/Prompt/permission/process-control code exists in Phase 1.
- [x] Full local test suite passes.
- [ ] Independent Sol QA accepted. **CHANGES REQUIRED**
- [x] Independent visual check of running dashboard passed; broader DPI/accessibility validation remains Phase 4.

### 1.4 Validation evidence

| Check | Command | Result |
|---|---|---|
| Baseline build | `dotnet build .\DSMonitor.csproj` | Passed before edits: 0 warnings, 0 errors |
| Phase 1 solution build | `dotnet build .\DSMonitor.slnx` | Passed locally: 0 warnings, 0 errors |
| Focused/full tests | `dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj` | Passed locally: 65/65 (62 unit/service + 2 UI smoke + 1 real-data integration), pending final rerun count |
| UI empty-state smoke | MSTest `MainWindow_LoadsLaysOutAndRendersWithoutException` | Passed; PNG rendered |
| UI populated-state smoke | MSTest `MainWindow_PopulatedStateRendersAndShowsRealDataControls` | Passed; overview + timeline PNGs rendered |
| Read-only check | `Run_ReadOnly_DoesNotModifyForgeFiles` | Passed |
| Unique logs / close check | `Run_RepeatedLifecycle_CreatesUniqueLogsAndClosesThem` | Passed |

### 1.5 Known limitations / risks

- forge-delegate 1.2.4 metadata has **no status field**. Therefore current real jobs display UNKNOWN by default; RUNNING/TIMED OUT require explicit future metadata/events. This is intentional and safer than PID inference.
- A past forge deadline is shown as wrapper-deadline information, not as worker termination. Historical completed jobs are not relabeled TIMED OUT without explicit evidence.
- Cost is available only when `step_finish.part.cost` appears in logs; otherwise it shows `—`.
- Tool Activity/Tests/Files tabs are Phase 2 placeholders; Phase 1 only has event summaries and a bounded Raw Log preview.
- Saved monitoring history browsing/replay is Phase 3. Phase 1 creates the files but does not provide a history UI.
- Settings is a disabled Phase 4 placeholder.
- Independent Sol QA returned CHANGES REQUIRED; Phase 1 must not be called COMPLETED until the findings below are repaired and revalidated.
- FileSystemWatcher is best-effort; periodic reconciliation is the correctness backstop.

**Unresolved independent review findings (2026-11-08):**

- **P1 — Path confinement:** `Services/PathSafety.cs:23–32` checks lexical paths only. A junction under the runs root can point outside it; a harmless temporary fixture confirmed outside content was readable. Reject or safely resolve reparse points before accepting monitored paths; add a regression test.
- **P2 — Event loss:** `Parsing/ForgeLogTailReader.cs:291–296` discards earlier events once a poll exceeds 500 events while advancing the file offset. An 800-event fixture returned 500 events, then zero on the next poll: 300 events never reach monitoring logs. Bound reads or preserve undelivered events; add an end-to-end logging regression.
- **P2 — Timeline order:** `ViewModels/JobRowViewModel.cs:153–160` sorts descending and inserts at index zero, reversing each batch despite the newest-first label. Correct the ordering and test multi-event batches.

**Independent validation:** `dotnet build DSMonitor.slnx` passed with zero warnings/errors; `dotnet test Tests/DSMonitor.Tests/DSMonitor.Tests.csproj --no-build` passed 65/65. These checks do not establish acceptance because the review reproductions above reveal coverage gaps. Source/test fingerprints remained unchanged throughout review.

**Repair limit:** worker reported both allowed focused repair cycles consumed. No further repair was delegated. Preserve this implementation and evidence for a bounded follow-up repair and independent revalidation.

**Phase completion gate:** resolve all findings and obtain independent QA acceptance. Phases 2–4 remain NOT STARTED.

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
| 1 | `PRJ-001..005`, `ARC-001..008`, `FR-001..046`, `UI-001..020`, `DATA-001..020`, `LIFE-001..015`, `LOG-001..011`, `SEC-001..015`, `ERR-001..011/014..021`, `PERF-001..010`, `TEST-001..017`, `ACC-P1-001..015` | Existing solution, forge runs | IN PROGRESS (QA pending) |
| 2 | `ARC-009`, `OC-001..010`, `FR-020`, `UI-013..014`, `ERR-012..015`, `TEST-018` | Phase 1 accepted, OpenCode service | NOT STARTED |
| 3 | `LOG-012`, `FR-006`, `UI-012`, `TEST-018`, export/privacy requirements | Phase 2 accepted, stored logs | NOT STARTED |
| 4 | `PRJ-005`, `PRJ-008`, `FR-045`, `UI-019`, `LOG-013`, `PERF-001..010`, `SEC-009..015`, `TEST-020` | Phase 3 accepted | NOT STARTED |

## Change Log

- **2026-11-08:** Reorganized requirements and roadmap; implemented Phase 1 and passed 65 tests/build. Independent Sol review returned CHANGES REQUIRED (path confinement, event loss, timeline order); running-app visual check passed. Phase 1 remains IN PROGRESS and Phases 2–4 remain NOT STARTED.
