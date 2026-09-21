---
title: 'CI gate: fail the build on a vulnerable transitive NuGet package'
type: 'chore'
created: '2026-09-21'
status: 'done'
route: 'one-shot'
---

# CI gate: fail the build on a vulnerable transitive NuGet package

## Intent

**Problem:** `dotnet restore`'s built-in NuGet Audit only *warns* (NU1901–NU1904) on a known vulnerability, direct or transitive — it never fails the build. That's the gap that let the SSH.NET CVEs (`spec-ssh-net-cve-fix.md`, PR #65) ship silently, transitively via `Testcontainers`, until a human happened to notice. Epic 6 retro action item #5, owned by Winston, no further deferring.

**Approach:** Add a root `Directory.Build.props` (none existed before) that sets `NuGetAuditMode=all` (scan transitive packages, made explicit rather than relying on Central Package Management's implicit default), `NuGetAuditLevel=moderate` (the reporting floor — NU1901/low never fires), and promotes `NU1902`/`NU1903`/`NU1904` to `WarningsAsErrors`. `NU1900` (audit couldn't run, e.g. registry unreachable) is deliberately left a warning so a transient outage doesn't block every PR. This runs during `dotnet restore`, which `pr-review.yml`'s `build-test-lint` job already does as its first step — no new CI step needed, the existing step becomes the gate.

**Verified:** a scratch project referencing `System.Net.Http` 4.3.0 (known high-severity CVE-2018-8292) failed restore with `NU1903` promoted to an error under this exact config; the real solution's `dotnet restore EnergyTracker.sln` still exits 0 (nothing currently vulnerable in the tree).

## Suggested Review Order

- New file, root-level so it auto-applies to every `.csproj` MSBuild discovers under it (including `spikes/3-8-bulk-write-throughput`, which opts out of `Directory.Packages.props` but not this).
  [`Directory.Build.props`](../../Directory.Build.props)
