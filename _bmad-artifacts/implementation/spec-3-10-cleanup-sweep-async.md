---
title: 'Move SmartPlugImportJobs sweep off the synchronous GET poll path'
type: 'bugfix'
created: '2026-09-17'
status: 'draft'
review_loop_iteration: 0
context: []
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** `ListSmartPlugImportJobs.ExecuteAsync` calls `SweepExpiredAsync` synchronously and inline on every `GET /api/smart-plug-import-jobs` call — the same request `job-history-list.tsx` polls every 8 seconds. Since `spec-3-10-cleanup-per-import-detach` (2026-09-12) made cleanup delete each large import's readings via hundreds of small sequential batched `UPDATE`s (`DetachReadingsForImportAsync`), a sweep that catches a terminal-state import old enough *and* large enough now runs that whole batched loop inline inside a GET request. This reintroduces, through a second, unaddressed entry point, the exact "operation too slow for a synchronous HTTP request" failure mode that `spec-3-10-cleanup-async-job` (round 3) was built to eliminate for the manual "clean up everything" button. Flagged **High priority** by adversarial review of `spec-3-10-cleanup-per-import-detach`; not yet a confirmed live incident, but the mechanism matches three of this feature's four confirmed incidents, and this entry point fires far more often than a manual click.

**Approach:** Stop doing deletion work inline on the read path. Two shapes were named by the review that raised this and neither has been chosen yet — see Ask First below.

## Boundaries & Constraints

**Always:** `ListSmartPlugImportJobs.ExecuteAsync`'s response latency must not scale with an eligible import's reading count, under either approach. FR-32/AD-6's six-state retention behavior (Success/Error/Flagged for Review fade out 30 days after completion; Waiting/Processing/Needs Mapping never auto-clear) must be preserved exactly — this is a where-the-sweep-runs change, not a what-gets-swept change.

**Ask First:** Which of these two approaches to build (HALT and confirm before any code change):
- **(a) Fire-and-forget:** `ListSmartPlugImportJobs` enqueues a background job (reusing the AD-6 queue and round-3's "reuse an already-Queued/Processing job instead of enqueueing a duplicate" dedup guard) when eligible rows exist, and returns without waiting. Mirrors the pattern already proven for the manual cleanup button. Open question this raises: is the existing dedup guard, built for one explicit user click, safe to reuse for a trigger that could now fire on every poll from every open tab?
- **(b) Bounded per-call chunking:** `SweepExpiredAsync` does at most one `DeleteBatchSize`/`DeleteReadingVolumeThreshold`-sized chunk of work per invocation, letting the next poll (≤8s later) continue where it left off — no single GET request's inline work is ever unbounded, but a fully-eligible import may take several polls to finish clearing.

Also confirm: under (a), a freshly-eligible row may legitimately reappear once more in a response while its cleanup job is still in flight (the current "swept row never appears in the same response that triggered its own sweep" guarantee would relax); under (b) that guarantee is preservable. Whichever is chosen, get sign-off that this tradeoff is acceptable before implementing.

**Never:** Do not restore an all-or-nothing synchronous transaction on this path — that's the exact regression round 3 fixed for the manual endpoint. Do not touch `DeleteJobsAsync`'s (manual "clean up everything") call path — it's already off the synchronous path; this spec is scoped to `SweepExpiredAsync`'s remaining exposure only.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| GET poll, nothing eligible | No terminal-state import older than 30 days | Response returns at current baseline latency | N/A |
| GET poll, one small eligible import | Terminal-state import needing few detach batches | Response returns at current baseline latency; cleanup completes without blocking the response | N/A |
| GET poll, one pathologically large eligible import (100k+ readings) | Terminal-state import needing hundreds of detach batches | Response returns at current baseline latency regardless of import size — this is the scenario that currently regresses | N/A |
| Concurrent polls from multiple open tabs of the same household | Two GETs arrive before the prior sweep/cleanup finishes | No duplicate cleanup job enqueued (approach a) / no double-chunking of the same rows (approach b) | N/A |

</frozen-after-approval>

## Code Map

- `src/EnergyTracker.Application/ListSmartPlugImportJobs.cs:42` -- current synchronous `await smartPlugImportRepository.SweepExpiredAsync(...)` call site to replace.
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:660` -- `SweepExpiredAsync`'s eligibility query (cheap, keep as-is) and its call into `DeleteEligibleAsync`.
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:886` -- `DeleteEligibleAsync`, the shared delete path also used by manual cleanup.
- `src/EnergyTracker.Infrastructure/Adapters/SmartPlugImportRepository.cs:807` -- `DetachReadingsForImportAsync`, the per-import batched-UPDATE loop whose cumulative cost is what makes this a synchronous-path problem.
- `src/EnergyTracker.Api/Endpoints/SmartPlugImportEndpoints.cs:186-197` -- reference: the existing manual-cleanup fire-and-forget enqueue + dedup-guard pattern (relevant if approach (a) is chosen).
- `src/EnergyTracker.Application/CleanUpSmartPlugImportJobs.cs` -- reference: the existing async-job payload/handler shape (relevant if approach (a) is chosen).

## Tasks & Acceptance

**Execution:**
- [ ] Resolve the Ask First decision above (approach a vs. b) with Winston/product owner -- blocks every other task; this spec's Tasks are intentionally left unscoped until that decision lands.

**Acceptance Criteria:**
- Given a household whose terminal-state Smart Plug import jobs include one requiring many detach batches, when `GET /api/smart-plug-import-jobs` is polled, then the response returns within this endpoint's existing baseline latency, with no synchronous multi-round-trip delete work executed inline in the request, regardless of which approach is implemented.
- Given the FR-32/AD-6 six-state retention rule, when the sweep runs (via either approach), then Success/Error/Flagged for Review rows still fade out at exactly 30 days post-completion and Waiting/Processing/Needs Mapping rows are never auto-cleared.

## Design Notes

`AD-6` already establishes exactly one worker processing background jobs — approach (a) doesn't introduce a new concurrency primitive, it reuses the existing queue. Approach (b) introduces a new "partial progress" state machine that today's `SweepExpiredAsync` doesn't have (it's currently all-or-nothing per call); if chosen, decide whether partial progress needs its own persisted cursor or can be re-derived from the eligibility query every time (likely the latter, since eligibility is re-queried each call already).

## Verification

**Commands:**
- `dotnet test tests/EnergyTracker.Infrastructure.Tests --filter "FullyQualifiedName~SmartPlugImportRepositoryTests"` -- expected: existing sweep/delete regression tests still pass after the change.
- `dotnet test tests/EnergyTracker.Application.Tests --filter "FullyQualifiedName~ListSmartPlugImportJobsTests"` -- expected: covers the new non-blocking behavior once implemented.
