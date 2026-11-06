---
baseline_commit: 2b33ac48fd681ed62c7bbcd8d28a640de679a239
---

# Story 11.32: Invite Trace Test Isolation

Status: done

<!-- Note: Validation is optional. Run validate-create-story for quality check before dev-story. -->
<!-- Epic 11 "Codebase audit hardening" (planning/epics/epic-11-codebase-audit-hardening.md), tier "Triage follow-ups", promoted from deferred-work.md on 2026-11-05 (entry from App Deploy run 41708528820, index row in test-coverage). Origin: the test `InviteTokenIsNotInTracesTests.Spans_show_the_marker_in_url_path_and_query_and_keep_the_route` (Story 11.22) failed the "Backend tests" job of App Deploy runs 41708528820 (2026-11-03) and 42103333770 (2026-11-05), so Deploy was skipped until the failed jobs were re-run. Architecture: AD-26 and ADR 0008 (the redaction the test proves). This file satisfies the spec-per-fix gate (project-context.md). -->
<!-- Branch: feature/11-32-invite-trace-test-isolation. ONE PR. No migration, no new NuGet or npm package, no frontend change, no `src/**` change expected. If the cause turns out to sit in `src/**`, stop and raise it: that is a production redaction question, not a test fix. -->
<!-- No Auth0/Chrome live gate: no auth flow, claim or browser behaviour changes. -->
<!-- Priority: this test blocks production deploys, so take it ahead of the other backlog stories. -->

## Story

As an operator,
I want the invite trace test to see only its own spans,
so that a deploy is not blocked by another test's timing and the test cannot pass without having seen the span it claims to check.

## Acceptance Criteria

1. **Cause proven before the fix.** **Given** the leading hypothesis in Dev Notes "Leading hypothesis", **when** this story starts, **then** the cause is shown by a run, not argued: the failure is reproduced (or a temporary diagnostic shows `exported` holding server spans from another host, with `/login` missing when `allArrived` fires), and the evidence is recorded in Debug Log References. If the hypothesis is wrong, stop and record what the run showed before changing the test.
2. **The wait is on this run's four spans.** **Given** `CapturingExporter` counts every server span it receives, **when** this story is done, **then** `allArrived` completes only after all four server spans of this run are exported, and spans from any other host or test neither complete the wait nor enter the list the assertions read.
3. **Both tests keep their strength.** **Given** the two facts in `InviteTokenIsNotInTracesTests`, **when** the fix ships, **then** no assertion is removed, loosened or made conditional, `No_exported_server_span_carries_the_token` still scans every span of the run including `/login`, and the test still proves `url.query` keeps its `?returnUrl=` prefix.
4. **No retry, no sleep.** No retry attribute, retry loop, longer timeout as the fix, `Task.Delay`, skip, or `continue-on-error` in a workflow. The 10-second wait stays a failure bound.
5. **Stable in the full suite.** **Given** the failure appears only in the full `EnergyTracker.Api.Tests` run, **when** the fix is in, **then** at least 10 consecutive full `EnergyTracker.Api.Tests` runs in `Release` pass locally, the count and result are recorded, and a known-bad variant (Dev Notes "Known-bad check") shows the isolation can fail.
6. **Shared helper unchanged for the log test.** `InviteTokenRequests.FireAsync` and `RedactedPathsAndQueries` keep their behaviour; `InviteTokenIsNotLoggedTests` stays green with no edit.
7. **Record kept.** The deferred-work entry and its index row leave `deferred-work.md` when the story is done (resolved: delete), and the next App Deploy runs its "Backend tests" job on the first attempt. One run is not proof, so a recurrence reopens this story.

## Tasks / Subtasks

- [x] Task 0: Baseline (AC: all). `dotnet build EnergyTracker.sln -c Release --no-incremental -p:TreatWarningsAsErrors=true`, `dotnet format EnergyTracker.sln --verify-no-changes --no-restore`, `dotnet test --project tests/EnergyTracker.Api.Tests -c Release`. Record counts (Story 11.25 reference: Api 508).
- [x] Task 1: Prove the cause (AC: #1). No committed code in this task.
  - [x] 1.1 Add a temporary diagnostic to `CapturingExporter` that records, at the moment `allArrived` fires, each server span's `url.path` and `TraceId`.
  - [x] 1.2 Loop the full `EnergyTracker.Api.Tests` run until the test fails, or capture foreign spans in a passing run. If it does not fail locally, raise load, for example with CPU pressure from a second parallel run, and say what you used.
  - [x] 1.3 Record the result. Remove the diagnostic.
- [x] Task 2: Failing test first (AC: #2, #5). Build the known-bad check so the current code fails it for the right reason, and record the red run.
- [x] Task 3: Isolate the run (AC: #2, #3, #4, #6). Apply Dev Notes "Fix to try first" in `InviteTokenIsNotInTracesTests.cs` only.
- [x] Task 4: Prove stability (AC: #5). Ten consecutive full `EnergyTracker.Api.Tests` runs in `Release`; record the result of each. Any failure sends the story back to Task 1.
- [x] Task 5: Records (AC: #7). Update `_bmad-artifacts/project-context.md` with one sentence if the fix adds a rule for capturing spans in a test. Delete the deferred-work entry and index row. Update the Change Log and File List.
- [x] Task 6: Full gate. `dotnet build` with warnings as errors, `dotnet format`, all four .NET test projects. Record in Debug Log References.

## Dev Notes

### Current state of the files this story touches

- `tests/EnergyTracker.Api.Tests/InviteTokenIsNotInTracesTests.cs`: `RunAsync` builds one host per test from the class fixture with `Otel:Exporter=otlp`, adds a `SimpleActivityExportProcessor` with `CapturingExporter`, fires four requests, then waits on `allArrived` for at most 10 s. `CapturingExporter` queues every activity and completes `allArrived` once `exported` holds `ExpectedServerSpans` (4) server spans of any origin. `RunAsync` then snapshots the server spans. A server span ends after the response reaches the client, which is why the test waits at all.
- `tests/EnergyTracker.Api.Tests/InviteTokenRequests.cs`: `FireAsync` sends `/join/{token}`, `/api/household-invites/{token}`, `.../accept`, then `/login?returnUrl=...`. `/login` is **last**, so its span is the one most likely to be missing from an early snapshot. `InviteTokenIsNotLoggedTests` shares this file.
- `tests/EnergyTracker.Api.Tests/xunit.runner.json` sets nothing, so test classes run in parallel (xunit v3 default). `SerialLoggingGroup` (`DisableParallelization = true`) serializes only the tests that capture Serilog events.
- `src/EnergyTracker.Api/Composition/ObservabilityRegistration.cs`, otlp branch: `AddAspNetCoreInstrumentation()` plus `AddInviteTokenRedaction()`. `src/EnergyTracker.Api/InviteTokenSpanRedaction.cs` holds the enrich hook. Neither should change.

### What is known

The failing line is the `/login` assertion (`InviteTokenIsNotInTracesTests.cs:81`), after the three `paths.ShouldContain` lines passed. Error from run 42103333770:

```
server.Where(a => Tag(a, "url.path") == "/login").Select(a => Tag(a, "url.query"))
    should contain an element satisfying the condition
q.StartsWith("?returnUrl=", Ordinal)
    but does not
```

Shouldly prints no actual values here, so the log does not say whether the `/login` span was missing or present with another query. The test passes alone, failed on `main` during Story 11.23 (PR #168, Api 507/508), and passed in the full local runs of Story 11.25.

### Leading hypothesis (prove it, Task 1)

The ASP.NET Core instrumentation subscribes to diagnostic sources process-wide, so a tracer provider in one test host can also receive server spans of other hosts running in parallel classes. `CapturingExporter` counts those foreign spans, so four can be reached before the `/login` span of this run ends. `allArrived` fires, `RunAsync` snapshots without `/login`, and the assertion fails. Alone, only this run's four spans exist, so the wait is correct. That fits every observation above, but no run has shown it yet.

This also means `No_exported_server_span_carries_the_token` can pass without seeing `/login`. Its `server.Count >= 4` check would be satisfied by foreign spans. A token leak on that span could go undetected, which makes this more than a flake.

### Fix to try first

Give the run its own W3C trace id and filter on it, in the test file only:

1. In `RunAsync`, create `ActivityTraceId.CreateRandom()` and add a `traceparent` header (`00-{traceId}-{spanId}-01`) to this run's `HttpClient` default headers after `InviteTokenRequests.CreateClient`. Do not put it in `CreateClient`: the log test shares that method.
2. Give `CapturingExporter` the trace id. It enqueues and counts only activities with that `TraceId`.
3. Keep `ExpectedServerSpans` and the 10-second bound. A span that never arrives then fails the wait, which is the correct failure.

Verify that the server span really takes this trace id through the test server and the OTel propagator; Task 1's diagnostic shows it. If it does not, use the fallback.

**Fallback:** `[Collection(SerialLoggingGroup.Name)]` on the class, the existing serial pattern. It is one line, but the group's name and comment speak of logging, so rename or add a sibling group only with a reason, and note that it hides crosstalk instead of removing it. Prefer the trace id.

**Do not:** retry, sleep, raise the timeout, drop the `/login` assertion, count four distinct `url.path` values from any host (a foreign `/login` would satisfy it), or change `src/**`.

### Known-bad check

Prove the filter matters: fire requests from a second host, or inject foreign server `Activity` objects into the exporter's batch path, while the first run is held before `/login`. Without the filter the wait completes early and the test fails. With it the foreign spans are ignored. Keep this as a test only if it stays deterministic; otherwise record it as a one-off red run in Debug Log References.

### Project Structure Notes

- Test class stays one file, `Snake_case` names, Shouldly, `TestContext.Current.CancellationToken`. No hand-rolled port fake is involved: `CapturingExporter` is an OTel exporter, not an Application port.
- No `Task.Delay` to make time pass (Story 11.17 rule).

### Previous story intelligence

- **11.22** added this test and recorded that `OpenTelemetry.Instrumentation.AspNetCore` 1.20.1 redacts `returnUrl` itself (`?returnUrl=Redacted`). If a run shows the `/login` span present with another query value, the cause is not the hypothesis above: look at that default first, and at `url.query` handling after a package bump.
- **11.23** first noted the failure as order-dependent (PR #168).
- **11.24** deploy needed a re-run of failed jobs (run 41708528820); **11.25** deploy needed the same (run 42103333770). The failed job was `Backend tests`, so Deploy was skipped and production stayed safe both times.

### Git intelligence

Recent work: `2b33ac4` (Story 11.25), `475176d` (close of 11.24). Neither touched this test. The first recorded failure (PR #168) predates the package bump `a297df5` (#171), so that bump did not start it.

### Latest tech notes

xunit v3 runs test collections in parallel by default, with each class as one collection unless a `[Collection]` says otherwise. Versions in use: OpenTelemetry 1.20.1 packages, `xunit.v3.mtp-v2` 3.2.2. No version change in this story.

### References

- [Source: _bmad-artifacts/implementation/deferred-work.md, "Deferred from: App Deploy run 41708528820 after merge of story 11-24"]
- [Source: _bmad-artifacts/implementation/11-22-keep-invite-token-out-of-logs.md]
- [Source: _bmad-artifacts/planning/epics/epic-11-codebase-audit-hardening.md, Story 11.32]
- [Source: _bmad-artifacts/project-context.md, "Request logs and spans (Story 11.22, S10)"]

### Project Context Reference

`_bmad-artifacts/project-context.md`: .NET testing rules, the Story 11.22 paragraph, the process gate for fixes to shipped code.

## Dev Agent Record

### Agent Model Used

claude-sonnet-5-5

### Debug Log References

- Baseline (Task 0, 2026-11-06): build with warnings as errors 0 warnings/0 errors, `dotnet format --verify-no-changes` clean, Api 508/508.
- Cause proof (Task 1, AC #1): temporary diagnostic in `CapturingExporter` appended every server span's `url.path@TraceId` to a scratch file when `allArrived` fired. Full `EnergyTracker.Api.Tests` Release loop, run 4 of 8 failed with `InviteTokenIsNotInTracesTests.cs:84` (the `/login` assertion; the 508-line baseline had passed). The dump of that run's exporter: `/health@b55bb90f... | /health@265e54ca... | /api/jobs/5ecc0c2e-...@42a5f894... | /api/households@e936b92f...`, four server spans of four different trace ids from other hosts, no `/join`, no `/api/household-invites`, no `/login`. Hypothesis confirmed; `src/**` not involved. Diagnostic removed (file restored from copy).
- Red (Task 2): new test `Spans_of_other_traces_are_dropped_and_spans_of_the_own_trace_are_kept` (exporter given four foreign server spans) failed on the unfiltered exporter with "spans of another trace completed the wait."
- Green (Task 3): exporter keeps and counts only activities with this run's `TraceId`; the run's client sends `traceparent: 00-{traceId}-{spanId}-01` (set in `RunAsync`, not in the shared `CreateClient`). The four-span wait passing in every run shows the server span takes the sent trace id; the fallback (serial collection) was not needed.
- Stability (Task 4, AC #5): 12 consecutive full `EnergyTracker.Api.Tests` Release runs, all 509/509, 0 failed. Before the fix: 1 failure in 8 runs.
- Full gate (Task 6): build with `-p:TreatWarningsAsErrors=true` 0 warnings/0 errors; `dotnet format --verify-no-changes` clean; Api 509, Application 612, Architecture 223, Infrastructure 567, all passed.

### Completion Notes List

- AC #1: cause shown by run (see Debug Log). AC #2: wait and list are limited to this run's trace id. AC #3: no assertion removed or loosened; `No_exported_server_span_carries_the_token` now scans only this run's spans, including `/login`, and its `Count >= 4` check can no longer be met by foreign spans. AC #4: no retry, sleep, longer timeout or skip. AC #5: 12 green runs plus the deterministic foreign-span test as the known-bad check (red on the unfiltered exporter, kept as a test). AC #6: `InviteTokenRequests.cs` untouched. AC #7: deferred-work entry and index row deleted; "next App Deploy runs Backend tests on first attempt" is open until the next deploy, a recurrence reopens the story.
- `project-context.md`: one sentence added to the Story 11.22 paragraph (span-capturing tests filter on their own trace id).

- Ultimate context engine analysis completed - comprehensive developer guide created

### Review Findings

- [x] [Review][Patch] `deferred-work.md` index still reads `test-coverage (17)`; section now has 16 rows [_bmad-artifacts/implementation/deferred-work.md:9]
- [x] [Review][Patch] `StartActivity(...)!` in foreign-span test can return null and fail with an NRE; throw a clear error instead [tests/EnergyTracker.Api.Tests/InviteTokenIsNotInTracesTests.cs:73]
- [x] [Review][Patch] Foreign-span test covers only the reject path; add one matching-trace span and assert it is enqueued and counts toward the wait [tests/EnergyTracker.Api.Tests/InviteTokenIsNotInTracesTests.cs:62]
- [x] [Review][Patch] 10 s `WaitAsync` timeout hides the cause if `traceparent` is not adopted; rethrow with a message naming trace propagation [tests/EnergyTracker.Api.Tests/InviteTokenIsNotInTracesTests.cs:55]
- [x] [Review][Defer] Wait completes on any 4 in-trace server spans, not on the 3 routes plus `/login`; a redirect hop could complete it early [tests/EnergyTracker.Api.Tests/InviteTokenIsNotInTracesTests.cs:30] — deferred, pre-existing count-based wait; 12 green runs show no extra spans today

### Change Log

- 2026-11-05: Story created, promoted from deferred-work.md after a second blocked deploy.
- 2026-11-06: Cause proven by diagnostic run; `CapturingExporter` filters on the run's trace id, client sends `traceparent`; foreign-span test added; deferred-work entry removed; project-context sentence added.

### File List

- `tests/EnergyTracker.Api.Tests/InviteTokenIsNotInTracesTests.cs` (modified)
- `_bmad-artifacts/project-context.md` (modified)
- `_bmad-artifacts/implementation/deferred-work.md` (modified: entry and index row removed)
- `_bmad-artifacts/implementation/sprint-status.yaml` (modified)
- `_bmad-artifacts/implementation/11-32-invite-trace-test-isolation.md` (modified)
