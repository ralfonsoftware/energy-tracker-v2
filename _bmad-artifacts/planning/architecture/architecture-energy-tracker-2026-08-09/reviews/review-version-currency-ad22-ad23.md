# Version-Currency Review — AD-22 & AD-23

**Reviewer role:** version-currency reviewer, BMad architecture-spine Reviewer Gate
**Target file:** `_bmad-artifacts/planning/architecture/architecture-energy-tracker-2026-08-09/ARCHITECTURE-SPINE/invariants-rules.md` (AD-22, AD-23)
**Cross-checked against:** `_bmad-artifacts/project-context.md`
**Method:** every technical/version claim re-derived from NuGet package pages, the upstream GitHub README/LICENSE, and web search — not taken on the spine text's word.

## Verdict

AD-22 makes no external technical/version claims (pure internal design) and is out of scope for this check. AD-23's core technical claims about `EFCore.BulkExtensions` — version, package split, provider mechanism, and the `BulkInsertOrUpdateAsync`/`UpdateByProperties`/`PropertiesToExclude` API shape — are all real and accurately described; the license one-liner is directionally correct but omits two of the license's three free-use qualifying paths and there is one concrete, previously-unflagged dependency-version friction point with AD-21 that should be called out before this ships.

## Findings

### 1. [MEDIUM] Package-split phrasing is imprecise: the umbrella package already pulls in all five providers
AD-23 says: *"a single `BulkInsertOrUpdateAsync` call (`EFCore.BulkExtensions` 10.0.1, `.SqlServer` + `.PostgreSql` sub-packages)"* — phrased as if referencing the main `EFCore.BulkExtensions` package plus the two provider sub-packages is what limits the footprint to SQL Server + Postgres.

Verified via NuGet dependency listings that this is not how the split works:
- `EFCore.BulkExtensions` 10.0.1 (the umbrella package) depends on **all four** provider adapters unconditionally: `EFCore.BulkExtensions.Oracle`, `.PostgreSql`, `.Sqlite`, `.SqlServer` (all `>= 10.0.1`).
- `EFCore.BulkExtensions.SqlServer` 10.0.1 depends only on `EFCore.BulkExtensions.Core (>= 10.0.1)`, `Microsoft.Data.SqlClient (>= 6.1.4)`, `Microsoft.EntityFrameworkCore.SqlServer.HierarchyId (>= 10.0.3)`, `NetTopologySuite.IO.SqlServerBytes (>= 2.1.0)`.
- `EFCore.BulkExtensions.PostgreSql` 10.0.1 depends only on `EFCore.BulkExtensions.Core (>= 10.0.1)` and `Npgsql.EntityFrameworkCore.PostgreSQL (>= 10.0.0)`.

So if the project actually references the umbrella `EFCore.BulkExtensions` package (as the AD's wording implies), it pulls in Oracle/SQLite adapters too — pointless bloat for a dual-provider (SQL Server + Postgres only) project, and arguably at odds with AD-2's "only the portable relational subset, no unnecessary surface" spirit. To get the intended SQL-Server-and-Postgres-only footprint, the project should reference `EFCore.BulkExtensions.Core` + `EFCore.BulkExtensions.SqlServer` + `EFCore.BulkExtensions.PostgreSql` directly and **skip** the umbrella `EFCore.BulkExtensions` package entirely. Worth a one-line correction in AD-23's rule text before this becomes the literal implementation guidance a story picks up.

### 2. [MEDIUM] Undisclosed dependency friction with AD-21's pinned `Microsoft.Data.SqlClient` resolution
`EFCore.BulkExtensions.SqlServer` 10.0.1 requires `Microsoft.Data.SqlClient >= 6.1.4`. This same file's AD-21 section states (line 148): *"Verified against the resolved build lockfile: `Microsoft.Data.SqlClient` resolves to `6.1.1`... the `Authentication=Active Directory *` connection-string modes work today with zero new NuGet references."*

`6.1.1 < 6.1.4`, so adding `EFCore.BulkExtensions.SqlServer` will force a transitive bump of `Microsoft.Data.SqlClient` to at least `6.1.4` — not a breaking change per se (NuGet will just resolve the higher version, and 6.1.x is within the same minor-version family AD-21 already discusses), but it does invalidate the specific "resolves to 6.1.1" number AD-21 pins as a verified fact, and AD-21's Entra auth story was predicated on that exact resolved version. This is a real, verifiable cross-AD consistency gap (not a hallucination) that the spine should reconcile — either re-verify AD-21's Entra ID auth flow still works cleanly once the lockfile moves to `Microsoft.Data.SqlClient >= 6.1.4`, or note the expected bump explicitly in AD-23 so it isn't a surprise when the lockfile changes.

### 3. [LOW] License summary is accurate but incomplete — two of three free-use paths are omitted
AD-23 states: *"free under the Community License at this project's personal/household scale (AD-13), commercial license required only for a company over $1M USD gross revenue."*

Verified against the upstream `LICENSE.txt` (github.com/borisdj/EFCore.BulkExtensions): the Community (free) License actually applies if **any one** of three independent conditions is met:
1. Company/individual with less than $1M USD annual gross revenue, **or**
2. Non-profit (non-governmental) organization or registered charity, **or**
3. Consuming the package for use in software licensed under a Free Open Source license.

Only failing *all three* triggers the commercial requirement. The AD's phrasing ("required only for a company over $1M") is not wrong for this project's own case (household-scale, for-profit-adjacent framing under AD-13), but it reads as if revenue is the *sole* axis, which understates how permissive the license actually is. No team-size, seat-count, or "must stay open source" gate exists on the free tier itself — commercial pricing tiers (once you *are* over the revenue threshold) are seat-banded (1-5 / 6-20 / 21-50 / 50+ developers), but that's a paid-tier pricing detail, not an eligibility condition. Recommend tightening the AD's one-liner to name at least the non-profit/FOSS alternate paths so a future reader doesn't assume revenue is the only lever.

### 4. [LOW/INFO] An actively-maintained MIT-licensed fork exists as a fallback if the license becomes a concern
`EFCore.BulkExtensions.MIT` (github.com/videokojot/EFCore.BulkExtensions.MIT, forked at the pre-cFOSS commit from Jan 2023) is a drop-in, fully MIT-licensed alternative — actively maintained (10.22.0 published 2025-12-09, ~880K downloads, parallel 8.x/9.x/10.x release lines). Not a correction to any claim in AD-23, but worth recording in the spine as the "if AD-23's license flag ever becomes a real blocker" escape hatch, since AD-23 already flags this as "the first non-Microsoft-licensed dependency of this kind in the stack; worth re-checking if the project's nature or scale ever changes."

### 5. [LOW/INFO] Known PostgreSQL `UpdateByProperties` caveat that AD-23's spike should explicitly target
The upstream README documents: *"With PostgreSQL, when matching is done, it requires a UniqueIndex; for custom `UpdateByProperties` without a unique index, one is temporarily created and the method cannot be in a transaction."* AD-20 (this same file) already establishes a real DB-level unique constraint on `(PowerPointId, IntervalStart)`, which should let the library detect and use that existing index rather than creating a temporary one on Postgres — but this is exactly the kind of provider-specific gotcha that could silently break the "no partial row survives cancellation" invariant AD-23's own "condition before wide rollout" spike is meant to catch. Recommend the spike explicitly confirm the library recognizes AD-20's existing unique index on Postgres (rather than falling back to a temporary index, which would make the operation not run inside a transaction) rather than leaving it as an implicit assumption.

## Verified facts (supporting detail)

| Claim in AD-23 | Verified | Source |
|---|---|---|
| `EFCore.BulkExtensions` 10.0.1 exists, targets .NET 10 / EF Core 10 | Yes | nuget.org/packages/EFCore.BulkExtensions (10.0.1, published 2026-02-25, net10.0 target) |
| Package split into `EFCore.BulkExtensions` / `.SqlServer` / `.PostgreSql` | Yes, but umbrella package pulls in Oracle/Sqlite too (see Finding 1) | nuget.org package dependency listings for all four packages |
| SQL Server mechanism: `SqlBulkCopy` for insert, `MERGE` for update/delete | Yes, verbatim in README | github.com/borisdj/EFCore.BulkExtensions README |
| PostgreSQL mechanism: `COPY BINARY` + `ON CONFLICT` for update | Yes, verbatim in README | github.com/borisdj/EFCore.BulkExtensions README |
| `BulkInsertOrUpdateAsync` with `UpdateByProperties` supports a composite/custom key, not just PK | Yes — documented feature, matches "like a unique constraint based on those cols" | github.com/borisdj/EFCore.BulkExtensions README; GitHub issue #131 |
| `PropertiesToExclude=[Id]` required alongside `UpdateByProperties` when an Identity column exists | Yes — documented requirement, not a hallucination | github.com/borisdj/EFCore.BulkExtensions README, referencing issue #131 |
| Dual license (cFOSS), free Community License, commercial only over $1M revenue | Accurate but incomplete (two more free-use paths exist) | github.com/borisdj/EFCore.BulkExtensions/blob/master/LICENSE.txt; GitHub issue #1079 (license-change announcement, Jan 2023); codis.tech/efcorebulk.html (commercial pricing tiers) |
| EF Core 10.0.10 (project's pinned version) is compatible with `EFCore.BulkExtensions` 10.0.1 | Yes | `EFCore.BulkExtensions.Core` 10.0.1 requires `Microsoft.EntityFrameworkCore.Relational >= 10.0.3`; project pins `10.0.10` (project-context.md line 33), which satisfies this. `EFCore.BulkExtensions.PostgreSql` requires `Npgsql.EntityFrameworkCore.PostgreSQL >= 10.0.0`; project pins `10.0.3` (project-context.md line 23) — also satisfied. |
| `Microsoft.Data.SqlClient` compatibility | Friction found — see Finding 2 | `EFCore.BulkExtensions.SqlServer` 10.0.1 requires `Microsoft.Data.SqlClient >= 6.1.4`; AD-21 (same file, line 148) states the resolved lockfile version is `6.1.1` |

## Sources

- https://www.nuget.org/packages/EFCore.BulkExtensions/10.0.1
- https://www.nuget.org/packages/EFCore.BulkExtensions.Core/10.0.1
- https://www.nuget.org/packages/EFCore.BulkExtensions.SqlServer/10.0.1
- https://www.nuget.org/packages/EFCore.BulkExtensions.PostgreSql/10.0.1
- https://www.nuget.org/packages/EFCore.BulkExtensions.MIT/
- https://github.com/borisdj/EFCore.BulkExtensions (README)
- https://raw.githubusercontent.com/borisdj/EFCore.BulkExtensions/master/README.md
- https://github.com/borisdj/EFCore.BulkExtensions/blob/master/LICENSE.txt
- https://github.com/borisdj/EFCore.BulkExtensions/issues/1079 (license-change announcement)
- https://github.com/borisdj/EFCore.BulkExtensions/issues/131 (UpdateByProperties + Identity/Id exclusion requirement)
- https://github.com/videokojot/EFCore.BulkExtensions.MIT (MIT fork)
- https://codis.tech/efcorebulk.html (commercial pricing tiers)
