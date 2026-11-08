# DSMonitor — Project Requirements

**Document version:** 2.0  
**Date:** 2026-11-08  
**Project:** DSMonitor (existing .NET 10 WPF solution, Windows 10/11)  
**Sources:** `docs/Project Requirements.md`, `docs/Plan.md`, `docs/FormView.png`, installed forge-delegate 1.2.4, OpenCode 2.2.6, and delegated task instructions.

> Phase status: **Phase 1 implementation is present and locally validated; independent Sol QA and visual QA are still pending.** Phase 2 has not started. This document organizes and clarifies the original requirements without silently removing or weakening them.

---

## 1. Project Overview and Goals

### 1.1 Purpose

DSMonitor is a polished, standalone Windows desktop application for continuously observing the existing workflow:

**Codex Desktop → forge-delegate → OpenCode → DeepSeek**

It exists so an operator can see what a long-running delegated task is doing **without interacting with, interrupting, or consuming additional AI tokens from the worker**.

DSMonitor is a **monitor, not an agent controller**.

### 1.2 Goals

| ID | Requirement | Phase |
|---|---|---|
| PRJ-001 | Build inside the existing DSMonitor .NET 10 WPF solution; do not replace the solution or create a separate application. | 1 |
| PRJ-002 | Display a finished-looking dark desktop dashboard, not a console embedded in a window. | 1 |
| PRJ-003 | Observe forge-delegate job metadata and logs without controlling forge, OpenCode, DeepSeek, or monitored projects. | 1 |
| PRJ-004 | Distinguish forge job status, OpenCode session status, and DSMonitor monitoring state at all times. | 1 |
| PRJ-005 | Prefer standard .NET/WPF APIs and minimal dependencies; keep parsing, monitoring, persistence, and UI responsibilities separate. | 1–4 |
| PRJ-006 | Add OpenCode read-only API observation only in Phase 2, after Phase 1 is accepted. | 2 |
| PRJ-007 | Add reusable history, replay, search, filtering, and safe export in Phase 3. | 3 |
| PRJ-008 | Add production hardening, settings, retention controls, accessibility, and complete regression checks in Phase 4. | 4 |

### 1.3 Non-goals

- No prompt submission, model invocation, delegation control, permission approval, session continuation, or configuration changes.
- No process liveness polling used as proof that a model is working.
- No fabricated sessions, progress percentages, milestones, costs, test results, or completion states.
- No telemetry or external service calls in Phase 1.
- No HTTP/OpenCode integration in Phase 1.

---

## 2. System Architecture and Integration Boundaries

### 2.1 Verified environment

| Component | Verified value | Consequence |
|---|---|---|
| DSMonitor | .NET 10 (`net10.0-windows`), WPF, Visual Studio 2026 | Existing solution retained. |
| forge-delegate | 1.2.4 | Metadata and JSONL logs are the Phase 1 data source. |
| OpenCode | 2.2.6 | REST integration deferred to Phase 2; actual OpenAPI schema must be inspected first. |
| Agent | `deepseek-bounded` | Displayed only when reported; no agent control. |
| Model steps | 120 | Historical/status context only. |
| Forge timeout | 90 minutes (5,400,000 ms) | Deadline is wrapper metadata only. |
| Runs root | `%USERPROFILE%\.forge-delegate\runs\` | Read-only observation root. |
| Monitoring logs | `%LOCALAPPDATA%\DSMonitor\Logs\` | DSMonitor-owned write location. |
| UI reference | `docs/FormView.png` | Visual direction only; content is not production data. |

### 2.2 Required architecture

| ID | Requirement | Phase |
|---|---|---|
| ARC-001 | Use MVVM with small model types and a thin code-behind for window lifecycle only. | 1 |
| ARC-002 | Separate forge metadata parsing, incremental log tailing, monitoring orchestration, monitoring-run logging, and UI state. | 1 |
| ARC-003 | Use asynchronous operations with `CancellationToken` for the monitoring loop and all repeatable lifecycle operations. | 1 |
| ARC-004 | Use incremental tail reading rather than rereading complete files every refresh. | 1 |
| ARC-005 | Use `FileSystemWatcher` plus periodic reconciliation so missed file events are recovered. | 1 |
| ARC-006 | Update WPF-bound state on the UI dispatcher; never mutate bound collections from background threads. | 1 |
| ARC-007 | Use `ObservableCollection`/`ICollectionView` and UI virtualization for job and timeline lists. | 1 |
| ARC-008 | Do not add HTTP/OpenCode clients or API calls in Phase 1. | 1 |
| ARC-009 | Introduce a read-only `HttpClient` integration only in Phase 2 after inspecting the installed OpenAPI schema. | 2 |
| ARC-010 | Avoid unnecessary abstractions, duplicated models, and heavyweight UI frameworks. | 1–4 |

### 2.3 Data flow

```text
%USERPROFILE%\.forge-delegate\runs\
  job_*.json  (metadata, read-only)
  job_*.log   (OpenCode JSONL, read-only)
        │
        ├── ForgeJobDiscovery ── metadata parser
        ├── ForgeLogTailReader ── incremental event parser
        ├── ForgeMonitoringService ── reconciliation + status resolution
        ├── MonitoringRunLogger ── .log / .jsonl safe summaries
        └── MainViewModel ── dispatcher ── WPF dashboard
```

### 2.4 Verified forge-delegate 1.2.4 format

Observed metadata fields (not guaranteed by the producer):

```json
{
  "id": "job_...",
  "pid": 1234,
  "model": "deepseek/deepseek-flash",
  "directory": "D:\\path\\to\\project",
  "started": 1794084044805,
  "deadline": 1794084944805,
  "logPath": "C:\\Users\\...\\runs\\job_....log",
  "usageLogged": true
}
```

Key verified facts:

- **There is no `status` field in current metadata.**
- Missing status remains **UNKNOWN**.
- `pid` alone is **not sufficient** evidence of RUNNING and must not be used as the Phase 1 status source.
- `deadline` identifies the forge wrapper deadline; it does **not** prove worker termination or continued execution.
- Logs are OpenCode JSONL events with fields such as `type`, `timestamp` (Unix ms), `sessionID`, and `part`. Observed event types include `step_start`, `step_finish`, `text`, and `tool_use`. Tool parts may include `tool`, `state.status`, `state.time`, and safe metadata. Step finishes may include `cost`, `tokens`, and `reason`.
- Logs can be appended to, replaced, truncated, locked, partial, or malformed while DSMonitor is reading them.

### 2.5 Phase boundary

| Capability | Phase 1 | Phase 2 | Phase 3 | Phase 4 |
|---|---|---|---|---|
| WPF dashboard, Start/Stop, IDLE | Yes | Preserve | Preserve | Polish |
| Forge metadata + JSONL log monitoring | Yes | Preserve | Preserve | Harden |
| OpenCode REST sessions/messages/tools | No | Yes | Preserve | Harden |
| Dated `.log`/`.jsonl` per run | Basic | Preserve | History/replay | Retention |
| Settings | Visible placeholder only | No change | No change | Implement |

---

## 3. Functional Requirements

### 3.1 Startup and STOPPED state

| ID | Requirement | Phase |
|---|---|---|
| FR-001 | Show the main window on startup. | 1 |
| FR-002 | Begin in STOPPED state with no background monitoring or worker connections. | 1 |
| FR-003 | Show a prominent **Start Monitoring** action. | 1 |
| FR-004 | Do not automatically connect to OpenCode, launch workers, or start forge jobs. | 1 |
| FR-005 | Do not show invented sessions, activity, costs, or test results while stopped. | 1 |
| FR-006 | Load saved DSMonitor history on startup only when Phase 3 history support exists; Phase 1 stops before implementing saved-history browsing. | 3 |

### 3.2 Start Monitoring

| ID | Requirement | Phase |
|---|---|---|
| FR-010 | On Start, create a new uniquely named monitoring log and JSONL file. | 1 |
| FR-011 | Inspect the existing forge runs directory. | 1 |
| FR-012 | Discover historical and newly created forge jobs. | 1 |
| FR-013 | Enter IDLE when no active job is confirmed, while making the number of UNKNOWN jobs visible. | 1 |
| FR-014 | Detect new `job_*.json` files automatically without restarting DSMonitor. | 1 |
| FR-015 | Read metadata and append-only log events incrementally. | 1 |
| FR-016 | Update the dashboard asynchronously without blocking the UI. | 1 |
| FR-017 | Record safe monitoring events into the current per-run logs. | 1 |
| FR-018 | Continue observing until Stop Monitoring or application exit. | 1 |
| FR-019 | Support multiple concurrent/observed forge jobs, not only one active DeepSeek session. | 1 |
| FR-020 | Correlate forge job IDs with OpenCode session IDs extracted from logs when available. | 1 (log-derived) / 2 (API) |

### 3.3 Stop Monitoring

| ID | Requirement | Phase |
|---|---|---|
| FR-030 | Stop only DSMonitor's watchers, polling, and active callback loop. | 1 |
| FR-031 | Close DSMonitor's monitoring log files cleanly. | 1 |
| FR-032 | Never stop, cancel, restart, resume, modify, or prompt forge/OpenCode/DeepSeek jobs. | 1 |
| FR-033 | Leave monitored files and logs untouched. | 1 |
| FR-034 | Remain responsive and support repeated Start/Stop cycles. | 1 |
| FR-035 | On application close during monitoring, cancel and close DSMonitor-owned resources. | 1 |

### 3.4 Job and event presentation

| ID | Requirement | Phase |
|---|---|---|
| FR-040 | Show forge job ID, project/directory, model/agent when available, status, start time, elapsed time, and last observed activity. | 1 |
| FR-041 | Selecting a job updates its detail panel. | 1 |
| FR-042 | Show actual observed JSONL events with timestamps, severity, category, and concise descriptions. | 1 |
| FR-043 | Support Overview, Timeline, Tool Activity, Tests, Files, and Raw Log tabs; later-phase tabs may be visible placeholders. | 1 shell / 2 data |
| FR-044 | Show a bounded runtime-only raw log preview in Phase 1; do not copy raw content into monitoring logs. | 1 |
| FR-045 | Do not fabricate milestones, progress percentages, completion summaries, or test outcomes. | 1–4 |
| FR-046 | Search/filter the job list by job, project, model, status, or session. | 1 |

---

## 4. WPF UI/UX Requirements

### 4.1 Visual direction

Reference: `docs/FormView.png` (design direction only; production UI must contain real data or honest empty states).

| ID | Requirement | Phase |
|---|---|---|
| UI-001 | Modern dark dashboard with clear typography, spacing, subtle borders, and status colors. | 1 |
| UI-002 | Professional desktop appearance; not an outdated Windows Forms look. | 1 |
| UI-003 | Responsive resizing, sensible minimum sizes, and high-DPI behavior. | 1 |
| UI-004 | Use standard WPF features and reusable styles; avoid heavyweight third-party UI frameworks. | 1 |
| UI-005 | Use `docs/FormView.png` as a design direction, not as hardcoded content. | 1 |

### 4.2 Layout

| ID | Requirement | Phase |
|---|---|---|
| UI-010 | Header shows DSMonitor name, monitoring state, Start, Stop, Settings access, current time, and last refresh. | 1 |
| UI-011 | Summary cards show active jobs, completed jobs, failed/timed-out jobs, total observed jobs, and available/observed cost without invention. | 1 |
| UI-012 | Jobs panel is searchable/filterable and shows all required columns. Selecting a row updates details. | 1 |
| UI-013 | Job details area is tabbed: Overview, Timeline, Tool Activity, Tests, Files, Raw Log. | 1 shell / 2 data |
| UI-014 | Overview shows job ID, session ID, project path, agent/model, start, duration/span, verified status, last activity, completion result when available, and blockers when observed. | 1 |
| UI-015 | Live Timeline shows actual events with timestamp, severity, category, and concise description. | 1 |
| UI-016 | Status bar shows monitoring state, forge directory availability, OpenCode connection state, refresh interval, last successful observation, and non-fatal errors. | 1 |
| UI-017 | Provide clear empty, waiting, unknown, stopped, and error states. | 1 |
| UI-018 | Show UNKNOWN counts prominently rather than guessing status. | 1 |
| UI-019 | Disable or mark planned controls/tabs honestly; Settings is a Phase 4 feature. | 1 |
| UI-020 | Do not hardcode screenshot content, fake sessions, fake usage statistics, or simulated live activity in production. | 1 |

### 4.3 Job columns

| Column | Required behavior |
|---|---|
| Job ID | Actual forge job identifier; monospace-safe display; tooltip. |
| Project/directory | Display leaf name with full path tooltip when available; missing shown as `—`. |
| Model/agent | Reported model string when available; missing shown as `—`. |
| Status | Forge job status only; colors distinguish UNKNOWN/RUNNING/DONE/FAILED/TIMED OUT/etc. |
| Start time | Reported start time only; missing shown as `—`. |
| Elapsed | Observed span since start for unknown/active jobs; observed span to last activity for terminal statuses; no invented completion time. |
| Last observed activity | Latest event timestamp or `—`. |

---

## 5. Forge-Delegate Data Sources

### 5.1 Discovery and metadata

| ID | Requirement |
|---|---|
| DATA-001 | Discover top-level `*.json` metadata files under the configured forge runs root. |
| DATA-002 | Read `id`, `pid`, `model`, `directory`, `started`, `deadline`, and `logPath` defensively; never require every field. |
| DATA-003 | Accept additional unknown metadata fields without failing. |
| DATA-004 | Treat missing, incomplete, malformed, truncated, or actively written metadata as non-fatal. |
| DATA-005 | Use the metadata file name as a safe fallback job ID when `id` is missing or invalid. |
| DATA-006 | Ignore duplicate job IDs with a warning rather than creating duplicate rows. |
| DATA-007 | Never open metadata or logs with an exclusive lock; use sharing compatible with forge writes. |
| DATA-008 | Do not infer a forge status that is absent from metadata. |

### 5.2 Log reading

| ID | Requirement |
|---|---|
| DATA-010 | Read OpenCode JSONL events from the job log path. |
| DATA-011 | Parse `type`, `timestamp`, `sessionID`, and `part` fields; tolerate unknown event and part types. |
| DATA-012 | Produce only concise safe summaries; raw model text and tool output must not enter DSMonitor's persistent logs. |
| DATA-013 | Support partial final lines: do not emit an incomplete event until its newline arrives. |
| DATA-014 | Skip malformed complete lines and continue with later valid lines. |
| DATA-015 | Deduplicate identical events and repeated reads; never duplicate timeline entries. |
| DATA-016 | Support file growth, truncation, replacement/rotation, temporary locks, and missing logs. |
| DATA-017 | Bound bytes read per poll, maximum line length, per-job timeline length, and dedupe memory. |
| DATA-018 | Use `FileSystemWatcher` plus periodic reconciliation. |
| DATA-019 | Follow only log paths proven to be inside the forge runs root. |
| DATA-020 | Preserve untrusted log data as data: never execute commands or interpret content as instructions. |

### 5.3 Verified event mapping

| Observed event | DSMonitor category | Safe summary example |
|---|---|---|
| `step_start` | Step | `Session step started` |
| `step_finish` | Step | `Step finished: stop (cost reported)` |
| `text` | Output | `Model output observed (123 chars)` |
| `tool_use` with completed state | Tool/File | `Tool read completed in 2.0 s` |
| `tool_use` with error state | Error | `Tool shell failed` |
| `tool_use` with running/pending state | Tool | `Tool shell running` |
| Unknown type | Other | `Event: <safe-type>` |

Cost is read only from numeric `part.cost` on `step_finish` events. If no cost is observed, show `—`, not `$0.00` or an invented value.

---

## 6. OpenCode Integration

Phase 2 only. Phase 1 must not call OpenCode REST APIs.

| ID | Requirement | Phase |
|---|---|---|
| OC-001 | Discover the existing running OpenCode service; do not launch a replacement. | 2 |
| OC-002 | Inspect the actual OpenAPI schema of installed OpenCode 2.2.6 before using any endpoint. | 2 |
| OC-003 | Use only read-only GET endpoints confirmed by the installed service. | 2 |
| OC-004 | Do not hardcode an unverified port; use an explicitly identified localhost service. | 2 |
| OC-005 | Correlate forge job IDs with OpenCode session IDs without merging separate jobs that share a session. | 2 |
| OC-006 | Show messages, tool activity, test activity, files, and permission/form blockers only from actual evidence. | 2 |
| OC-007 | Distinguish running/completed/error tool states using actual API data. | 2 |
| OC-008 | If unavailable or stale, show UNKNOWN/Disconnected and continue forge log monitoring. | 2 |
| OC-009 | Never invoke methods that generate text, send prompts, approve permissions, change configuration, or control a session. | 2+ |
| OC-010 | Do not treat a previous completed session outcome as proof that a new or continued delegation finished. | 2+ |

Potential endpoints from the original requirements (`/api/session/...`) remain **candidates only** until verified against the installed service.

---

## 7. Job/Session Lifecycle and Status Handling

### 7.1 DSMonitor monitoring states

| State | Meaning |
|---|---|
| STOPPED | DSMonitor is not watching or polling. |
| IDLE | Monitoring is active, but no job is confirmed active. UNKNOWN jobs may still be visible. |
| MONITORING | Monitoring is active and at least one job is confirmed active (RUNNING or BLOCKED by explicit evidence). |
| ERROR | The monitoring pipeline encountered an unexpected error; non-fatal issues are shown in the status bar instead. |

These states are separate from forge job status and OpenCode worker/session status.

### 7.2 Forge job statuses

| Status | Meaning | Phase 1 source |
|---|---|---|
| UNKNOWN | Missing, contradictory, or unavailable information. | Default when metadata has no status. |
| RUNNING | Active execution is confirmed. | Explicit metadata/status evidence. |
| DONE | Corresponding forge job finished. | Explicit reported completion. |
| FAILED | Actual failure reported. | Explicit reported failure. |
| INTERRUPTED | Session was interrupted. | Explicit reported interruption. |
| TIMED OUT | Forge wrapper timeout is explicitly reported. | Explicit timeout status/event, or a verified wrapper status. |
| BLOCKED | Permission/form/explicit blocker observed. | Explicit blocker evidence. |

### 7.3 Status rules and clarifications

| ID | Requirement |
|---|---|
| LIFE-001 | Keep DSMonitor monitoring state, forge job status, and OpenCode session state distinct. |
| LIFE-002 | A forge timeout does not necessarily terminate the OpenCode worker. |
| LIFE-003 | A silent log does not mean the worker is idle or inactive. |
| LIFE-004 | Process liveness (PID) alone is not sufficient evidence of RUNNING in Phase 1. |
| LIFE-005 | Missing status remains UNKNOWN; do not infer DONE from a dead PID, a quiet log, or a past deadline. |
| LIFE-006 | A passed deadline is displayed as wrapper deadline information and never as proof of worker termination. |
| LIFE-007 | If no active job is confirmed, monitoring state is IDLE even if UNKNOWN jobs exist. |
| LIFE-008 | The UI must make the UNKNOWN count visible in IDLE. |
| LIFE-009 | Forge job ID and OpenCode session ID are different identifiers. |
| LIFE-010 | Multiple forge jobs may share one OpenCode session; do not merge the jobs. |
| LIFE-011 | One job may expose a session ID learned from its log; keep that relationship explicit. |
| LIFE-012 | Do not treat an old completed session as proof that the current delegation finished. |
| LIFE-013 | Multiple concurrent jobs must remain independently selectable and observable. |
| LIFE-014 | Repeated Start/Stop and application close during monitoring must leave no active watchers or open log writers. |
| LIFE-015 | Status transitions are logged only when actual source evidence changes. |

**Clarification on TIMED OUT (not a removal):** The original definition says TIMED OUT means the forge deadline elapsed. Verification of forge 1.2.4 showed that historical metadata contains a past `deadline` for jobs that may have completed normally, while status is absent. Applying deadline-alone to job status would falsely mark historical completed jobs as timed out. DSMonitor therefore shows a passed deadline as a wrapper-deadline warning and keeps the job status UNKNOWN unless a timeout is explicitly reported. The TIMED OUT status remains supported for explicit evidence.

---

## 8. Monitoring History and Logging

### 8.1 Per-run logging

| ID | Requirement | Phase |
|---|---|---|
| LOG-001 | Create a new uniquely named monitoring log every time Start Monitoring is pressed. | 1 |
| LOG-002 | Write under a writable per-user location, preferably `%LOCALAPPDATA%\DSMonitor\Logs\`. | 1 |
| LOG-003 | Use a dated folder layout and unique base name; example: `Logs/2026-11-08/DSMonitor_2026-11-08_19-30-00_ab12cd.log`. | 1 |
| LOG-004 | Write a human-readable `.log` and structured JSON Lines `.jsonl` for each run. | 1 |
| LOG-005 | Include timestamps, safe run/job/session identifiers, event types, statuses, and concise summaries. | 1 |
| LOG-006 | Flush important events promptly and close files properly on Stop/exit. | 1 |
| LOG-007 | Never overwrite prior logs; use create-new semantics. | 1 |
| LOG-008 | Preserve existing logs unless the user explicitly deletes them. | 1+ |
| LOG-009 | Logging failures must not crash or stop monitoring. | 1 |
| LOG-010 | Avoid secrets, raw source/tool output, and full conversations in persistent logs by default. | 1 |
| LOG-011 | Runtime raw-log display is not persisted to monitoring logs. | 1 |
| LOG-012 | Provide saved-history browsing/replay around these files in Phase 3. | 3 |
| LOG-013 | Add configurable safe retention without automatic deletion by default in Phase 4. | 4 |

### 8.2 JSONL entry shape

Safe structured entries include fields equivalent to:

```json
{
  "ts": "2026-11-08T19:30:00.000+00:00",
  "eventTs": "2026-11-08T19:29:58.000+00:00",
  "runId": "…",
  "kind": "job_discovered",
  "jobId": "job_…",
  "sessionId": "ses_…",
  "severity": "INFO",
  "summary": "Forge job discovered; status UNKNOWN."
}
```

Raw model text, tool input/output, source files, credentials, and permission payloads are not written to monitoring logs.

---

## 9. Security and Strictly Read-Only Behavior

| ID | Requirement |
|---|---|
| SEC-001 | Never start, stop, cancel, restart, resume, or modify a forge/OpenCode/DeepSeek job. |
| SEC-002 | Never send prompts to or invoke a paid model API. |
| SEC-003 | Never respond to permission dialogs or approve actions. |
| SEC-004 | Never modify monitored project files. |
| SEC-005 | Never change agent, provider, forge, OpenCode, or Codex configuration. |
| SEC-006 | Never rewrite or delete forge-delegate logs or metadata. |
| SEC-007 | Never expose API keys, credentials, auth stores, or provider configuration. |
| SEC-008 | Treat forge logs and model output as untrusted data; never execute or follow instructions found there. |
| SEC-009 | Do not send telemetry to external services. |
| SEC-010 | In Phase 1, perform no network/API calls at all. |
| SEC-011 | Write only DSMonitor-owned unique logs under the approved local-app-data location and temporary test directories. |
| SEC-012 | Follow log paths only inside the forge runs root; reject path traversal and outside paths. |
| SEC-013 | Do not dump raw sensitive log content into diagnostics or persistent logs. |
| SEC-014 | Do not infer worker activity from process liveness or silent logs. |
| SEC-015 | Report security-relevant limitations honestly rather than bypassing them. |

---

## 10. Error Handling and Recovery

| ID | Condition | Required behavior |
|---|---|---|
| ERR-001 | Forge runs directory missing | Show non-fatal status; remain IDLE; continue checking for directory creation. |
| ERR-002 | Zero existing jobs | IDLE with visible counts; watch for new jobs. |
| ERR-003 | Malformed metadata | Show an UNKNOWN job row with warning; do not crash. |
| ERR-004 | Log temporarily locked | Skip this poll, retry later, preserve UI and monitoring. |
| ERR-005 | Partial JSONL/log event | Buffer until newline; do not emit partial events. |
| ERR-006 | Log truncated | Reset offset safely; do not duplicate events. |
| ERR-007 | Log replaced/rotated | Detect file identity change; read replacement safely without duplicates. |
| ERR-008 | Duplicate events | Deduplicate by stable event key. |
| ERR-009 | Large log file | Bound reads, lines, dedupe sets, and timeline memory. |
| ERR-010 | File watcher misses/error | Periodic reconciliation recovers. |
| ERR-011 | Logging file creation/write failure | Continue monitoring; surface non-fatal warning. |
| ERR-012 | OpenCode service unavailable | Phase 2: show UNKNOWN/Disconnected and keep forge monitoring. |
| ERR-013 | OpenCode service restarted | Phase 2: reconnect/reconcile without controlling it. |
| ERR-014 | Session ID missing | Show not reported; keep job observable. |
| ERR-015 | Invalid/stale session status | Show UNKNOWN rather than inventing current state. |
| ERR-016 | Forge timeout but OpenCode active | Show wrapper timeout information; do not claim worker termination. |
| ERR-017 | Multiple concurrent jobs | Keep independent rows and statuses. |
| ERR-018 | User clicks Start/Stop repeatedly | Serialize lifecycle; ignore redundant starts; close each run cleanly. |
| ERR-019 | Application closes during monitoring | Cancel, flush, close DSMonitor resources, leave external jobs untouched. |
| ERR-020 | History loading failure (Phase 3) | Show an error state without losing live monitoring. |
| ERR-021 | Unexpected monitoring scan exception | Show ERROR with last known data and continue periodic recovery where safe. |

---

## 11. Performance Requirements

| ID | Requirement |
|---|---|
| PERF-001 | Incremental append reading instead of full rereads each refresh. |
| PERF-002 | Bound bytes read per job per poll (Phase 1: 512 KiB default). |
| PERF-003 | Bound line length and discard pathological lines safely. |
| PERF-004 | Bound per-job timeline events (Phase 1: 500) and dedupe memory. |
| PERF-005 | Keep file I/O and parsing off the UI thread. |
| PERF-006 | Use job/timeline UI virtualization for large lists. |
| PERF-007 | Update only changed job/event rows in the view model. |
| PERF-008 | Avoid holding locks on forge-owned files. |
| PERF-009 | Handle multiple jobs without blocking one job's scan on another's I/O. |
| PERF-010 | Do not duplicate the entire OpenCode conversation into DSMonitor logs or memory. |

---

## 12. Automated Testing Requirements

| ID | Requirement | Coverage |
|---|---|---|
| TEST-001 | Test valid forge metadata parsing. | Phase 1 tests |
| TEST-002 | Test missing, malformed, wrong-type, and path-escaping metadata. | Phase 1 tests |
| TEST-003 | Test missing runs directory and zero jobs. | Phase 1 tests |
| TEST-004 | Test incremental log reading and append handling. | Phase 1 tests |
| TEST-005 | Test partial events and malformed lines. | Phase 1 tests |
| TEST-006 | Test duplicate prevention across polls and replacement. | Phase 1 tests |
| TEST-007 | Test truncation and file replacement. | Phase 1 tests |
| TEST-008 | Test maximum read bounds and runtime preview bounds. | Phase 1 tests |
| TEST-009 | Test explicit status mapping, missing status, deadline behavior, and monitor state. | Phase 1 tests |
| TEST-010 | Test new-job detection without restart. | Phase 1 tests |
| TEST-011 | Test job/session correlation and distinct jobs sharing a session. | Phase 1 tests |
| TEST-012 | Test Start/Stop lifecycle, cancellation, closed logs, repeated runs. | Phase 1 tests |
| TEST-013 | Test unique log creation, JSONL shape, safe summaries, and no overwrite. | Phase 1 tests |
| TEST-014 | Test logging failure does not stop monitoring. | Phase 1 tests |
| TEST-015 | Test read-only behavior (forge files unchanged). | Phase 1 tests |
| TEST-016 | Test UI state projection, UNKNOWN visibility, selections, and no duplicate timeline events. | Phase 1 tests |
| TEST-017 | Smoke-test XAML loading/layout and render empty/populated states without exceptions. | Phase 1 tests |
| TEST-018 | Test job/session correlation, OpenCode mocks, history replay, and full regression. | Phase 2/3 |
| TEST-019 | Use fixtures/mock data or existing completed sessions; never launch paid model sessions for tests. | All phases |
| TEST-020 | Do not weaken or silently modify existing tests to make them pass. | All phases |

**Phase 1 test framework:** MSTest (standard .NET test framework) with `Microsoft.NET.Test.Sdk`, targeting `net10.0-windows`. Tests write only isolated temporary directories.

Required validation commands:

```powershell
dotnet build .\DSMonitor.slnx
dotnet test .\tests\DSMonitor.Tests\DSMonitor.Tests.csproj
```

A phase is not complete merely because code compiles.

---

## 13. Phase-Specific Acceptance Criteria

### 13.1 Phase 1 acceptance

| ID | Criterion | Phase 1 status |
|---|---|---|
| ACC-P1-001 | Existing DSMonitor project builds on .NET 10. | Implemented; local validation passed. |
| ACC-P1-002 | MainWindow presents a polished dark dashboard based on the reference direction. | Implemented; visual QA pending. |
| ACC-P1-003 | Start Monitoring begins observing forge-delegate data. | Implemented; service/view-model tests passed. |
| ACC-P1-004 | With no active jobs, the application remains IDLE and waits. | Implemented and tested; UNKNOWN count visible. |
| ACC-P1-005 | A new forge job is discovered without restarting DSMonitor. | Implemented and tested. |
| ACC-P1-006 | Historical job records can be displayed. | Implemented from current runs root; tested with fixtures. |
| ACC-P1-007 | Selecting a job shows actual available metadata/events. | Implemented and rendered in smoke test. |
| ACC-P1-008 | Stop Monitoring stops only DSMonitor. | Implemented and tested read-only. |
| ACC-P1-009 | Every Start creates a uniquely dated monitoring log. | Implemented and tested. |
| ACC-P1-010 | Monitoring does not initiate external AI calls. | Implemented; no HTTP client in Phase 1. |
| ACC-P1-011 | Missing or malformed files do not crash the UI. | Implemented and tested. |
| ACC-P1-012 | Relevant tests pass and results are reported. | 65 local tests passed before independent QA; report pending. |
| ACC-P1-013 | Independent Sol QA completed. | **PENDING** |
| ACC-P1-014 | Independent visual QA completed. | **PENDING** |
| ACC-P1-015 | Phase 1 formally marked COMPLETED. | **NOT YET — pending ACC-P1-013/014.** |

### 13.2 Definition of phase completion

A phase is COMPLETED only when:

1. Implementation tasks are checked.
2. Required focused tests pass.
3. Relevant regression suite passes.
4. The solution builds.
5. Independent QA (Sol for Phase 1) accepts the behavior.
6. Visual QA accepts UI changes where applicable.
7. Remaining risks/limitations are documented.

### 13.3 Future phase acceptance summaries

| Phase | Acceptance summary | Status |
|---|---|---|
| Phase 2 — OpenCode live monitoring | Verified read-only API; session correlation; real tool/test/file/permission evidence; graceful unavailable states; tests/build. | NOT STARTED |
| Phase 3 — History and diagnostics | Saved runs browsable; replay/search/filter/export; no overwrite; persistence tests. | NOT STARTED |
| Phase 4 — Production hardening | Reliable recovery, multiple sessions, settings, privacy, UI polish, full regression. | NOT STARTED |

---

## 14. Requirement Decisions and Clarifications

These decisions preserve the original intent and explain implementation choices made after inspecting actual installed formats.

| Decision | Rationale |
|---|---|
| Missing forge status is UNKNOWN. | forge-delegate 1.2.4 metadata has no status field. Inferring from PID or silence would be unsafe. |
| PID is not used for RUNNING. | The task explicitly forbids process liveness as sufficient evidence of execution. |
| Deadline is wrapper metadata. | A past deadline is surfaced but does not by itself prove worker termination or mark a historical completed job timed out. TIMED OUT remains supported for explicit reports. |
| OpenCode REST is excluded from Phase 1. | Preserves phase boundaries; no HTTP client or unverified endpoint use. |
| Cost is displayed only from observed `step_finish.cost`. | Avoids invented metrics. Missing cost is `—`. |
| Raw log preview is runtime-only. | Satisfies the Raw Log tab without persisting sensitive output. |
| Tool Activity, Tests, and Files tabs are placeholders in Phase 1. | Their data requires Phase 2 API evidence. |
| Settings button is a disabled Phase 4 placeholder. | Header requires Settings access; settings behavior is Phase 4. |
| Startup does not load saved history in Phase 1. | Historical monitoring-run browsing is Phase 3; live forge jobs are still discovered from the runs directory after Start. |
| Log paths are confined to the configured runs root. | Metadata is untrusted; prevents following arbitrary paths. |
| Logging failures are non-fatal. | Monitoring reliability must not depend on DSMonitor's own log writer. |
| Both `.log` and `.jsonl` are created in Phase 1. | The task requires the first per-run monitoring log and future replay foundation; full history UI remains Phase 3. |

### 14.1 Requirement change log

- **2026-11-08 — Document reorganization:** Added stable IDs, tables, phase assignments, acceptance criteria, and traceability. Retained original functional scope.
- **2026-11-08 — Status clarification:** Documented verified absence of forge status and the UNKNOWN/deadline rules. TIMED OUT remains for explicit evidence.
- **2026-11-08 — Phase assignment clarification:** Settings = Phase 4; full history/replay/search/export = Phase 3; OpenCode REST/tool/test/files/permission details = Phase 2; Phase 1 includes disabled placeholders and logging foundation.
- **2026-11-08 — Cost clarification:** Phase 1 observed cost is log-derived only; broader cost/cache summaries remain Phase 4.
