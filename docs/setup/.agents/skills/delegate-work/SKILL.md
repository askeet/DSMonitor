---
name: delegate-work
description: Use Forge Delegate for authorized external execution of bounded tasks, including implementation, debugging, tests, audits, and review. For coding-related requests, load cost-efficient-coding as the controlling workflow.
---

# Forge Delegate execution

Forge Delegate runs an external worker through a supported tool interface. Its available provider, model, file access, cost, context use, and execution behavior depend on the current runtime; do not infer them from defaults or another host.

## Load the controlling workflow

For software development, including debugging, implementation, testing, code audits, or review, read [cost-efficient-coding](../cost-efficient-coding/SKILL.md) if available and not already active. It controls task routing, project authorization, provider/worker/model selection, architecture and audit gates, worker ownership, repair budgets, independent QA, security, acceptance, and usage reporting. Use this skill only for Forge Delegate mechanics. When cost-efficient-coding loaded this skill, do not reload it; do not reread either skill without a concrete need. If the controlling skill is unavailable, do not claim it was loaded; follow applicable user and platform requirements and report the material workflow limitation.

For noncoding delegation, use these mechanics without imposing the full coding workflow. Honor the user's requested provider and scope, source-sharing boundaries, and platform approvals. Never infer authorization for installations, system/security changes, elevation, destructive operations, or unrelated private data from a delegation request. Report missing authorization or readiness rather than bypassing it.

## Prepare a supported call

Inspect the current supported delegation tool schema and accessible configuration/capabilities before use; reuse applicable verified evidence. For coding tasks, follow the controlling skill's readiness and root `AGENTS.md` authorization process rather than redefining it here. Do not use a paid worker call as a capability or authentication probe. Supply explicit supported `model`, `directory`, `agent`, or other selections when the controlling workflow requires them; never omit them merely because a default exists or assume a default is cheap, free, authenticated, or authorized. Verify the worker's actual access to task files instead of assuming it can read the whole project. If a user-requested provider or necessary capability is unavailable, report that fact rather than silently substituting another provider or native implementation.

Give the worker a bounded task with goal, acceptance criteria, allowed paths, exclusions, applicable contracts, checks, and evidence to return. Prefer paths and the minimum necessary context over source dumps, conversation histories, or unrelated logs. State whether the assignment is read-only or permits edits. For surveys and audits, ask for structured, path-backed findings, including relevant absences and uncertainty. Ground important claims in source and spot-check them before adopting the conclusion; for coding, the controlling workflow owns the verdict and any required independent QA.

## Execute, track, and continue

Invoke the supported `delegate` operation with the verified selections and approval settings; do not invent parameters. A synchronous call yields a result when it completes. If the current runtime supports background execution, capture its job identifier and any session identifier, then await a completion notification or use the supported status/result operation at the controlling workflow's cadence when it applies, otherwise at proportionate intervals. Retrieve completed or failed results promptly. Do not assume another host's background threshold or that a timeout stopped the worker.

Record job/session identifiers. For coding, follow the controlling workflow's ownership and monitoring cadence; for standalone noncoding work, the caller tracks the job until completion or an explicitly supported transfer. For an authorized correction or continuation, reuse the same session when supported, append the bounded new instruction, and retain identifiers and remaining limits. The controlling workflow decides whether coding continuation and repairs are allowed; unsupported transfer or session reuse is a reported limitation, not a reason to start overlapping work.

## Return evidence and errors

Return the actual completion status, job/session identifiers, changed paths and diff, path-backed findings, checks with commands/results, limitations, and available usage provenance. Review the actual result and diff; a worker summary alone is not acceptance. For coding tasks, preserve the controlling skill's self-review and independent QA requirements.

On an operational error or unsupported capability, report the tool, action, observed error, and worker state. Do not guess APIs, retry an equivalent rejected call, bypass permissions, or silently switch execution strategy. Defer decisions on fallback, repair allowance, and acceptance to the controlling workflow when it applies.
