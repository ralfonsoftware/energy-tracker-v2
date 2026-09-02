# Version-Currency Review — AD-21 (Azure SQL access via Microsoft Entra ID-only authentication)

**Reviewed:** `ARCHITECTURE-SPINE/invariants-rules.md`, AD-21 (lines 133–141)
**Method:** Live research against Microsoft Learn (via the Microsoft Docs MCP server) and general web search, dated 2026-09-02. Also cross-checked against the repo's actual `.github/workflows/app-deploy.yml` migration step for consistency with the described cutover flow.
**Verdict:** AD-21's technical claims are accurate and current as researched — no hallucinated APIs, no stale syntax, no deprecated resource types. One real gap: the AD is silent on a documented latency characteristic of `Authentication=Active Directory Default` in a non-Azure CI runner, which is a known, Microsoft-documented behavior that should be called out rather than left implicit.

---

## 1. `Authentication=Active Directory Managed Identity` (system-assigned, no `User Id`)

**Verified current.** Microsoft Learn's ["Connect to Azure SQL with Microsoft Entra authentication and SqlClient"](https://learn.microsoft.com/sql/connect/ado-net/sql/azure-active-directory-authentication) confirms this keyword has been supported since `Microsoft.Data.SqlClient` 2.1.0 and is unchanged through the current 7.x line. For a **system-assigned** managed identity, no `User Id` is required — only a user-assigned identity needs the `User Id=<client-id>` parameter (client ID since v3.0+, object ID before that). AD-21's claim matches exactly.

Example confirmed from Microsoft's SqlPackage docs: `Server=sampleserver.database.windows.net; Authentication=Active Directory Managed Identity; Database=sampledatabase;`

**Severity if wrong:** would have been critical (wrong connection string = total auth failure). Confirmed correct — no finding.

## 2. `Authentication=Active Directory Default` and the `AzureCliCredential` fallback

**Verified current, with one omitted nuance (medium severity).**

- The keyword itself is correct and has been supported since `Microsoft.Data.SqlClient` 3.0.0.
- Microsoft.Data.SqlClient's internal `DefaultAzureCredential` chain **does** include `AzureCliCredential`, confirmed via the official release notes for v3.0 and the current Entra-authentication doc's credential list: `EnvironmentCredential → WorkloadIdentityCredential → ManagedIdentityCredential → SharedTokenCacheCredential → VisualStudioCredential → VisualStudioCodeCredential → AzurePowerShellCredential → AzureCliCredential → AzureDeveloperCliCredential`. So an already-authenticated `az login` (or, as in this repo's actual `app-deploy.yml`, the `azure/login@v3` OIDC federated-credential action, which performs the CLI-equivalent login under the hood) on a GitHub Actions runner would indeed be picked up.
- **The gap:** Microsoft's own ["Authentication best practices with the Azure Identity library for .NET"](https://learn.microsoft.com/dotnet/azure/sdk/authentication/best-practices) explicitly documents that `DefaultAzureCredential` on a non-Azure host pays a real latency cost — `ManagedIdentityCredential` is tried *before* `AzureCliCredential` and must fail (via an IMDS probe) before the chain falls through. The Entra-authentication doc itself carries an explicit warning: *"'Active Directory Default' isn't recommended for environments that have strict service level response times."* Community-reported numbers (GitHub `dotnet/SqlClient` issues #1403, #1473, #2072, #2149) put this added latency at anywhere from ~1s to ~10s on a first connection.
- **Why this doesn't break anything here, but should still be named:** the repo's actual migration step (`app-deploy.yml` lines 164–207) already budgets `Command Timeout=600` and `timeout-minutes: 20` for the migration job — so a few extra seconds of credential-chain latency is immaterial in practice. But AD-21 currently presents `Active Directory Default` purely as "rides the existing `az login` session," with no acknowledgment that this specific combination (non-Azure runner + `DefaultAzureCredential`) is a documented degraded-performance case Microsoft itself flags. A future reader tuning connection/command timeouts for the *new* Entra-based migration connection string (replacing the current SQL-auth one) could reasonably read AD-21 and assume the credential resolution is instant, and set a tight `Connection Timeout` that then flakes intermittently in CI.
- No ordering/timeout issue exists that would make it **not work** — only one that adds latency. AD-21's causal claim ("riding the `az login` OIDC session ... `DefaultAzureCredential`'s `AzureCliCredential` fallback") is technically correct.

**Recommendation:** add a one-line note to AD-21 acknowledging the `ManagedIdentityCredential`-probe-then-fallback latency on the CI runner, and confirm the new Entra-based migration connection string keeps a `Connection Timeout` generous enough to absorb it (the existing 30s SQL-auth value would very likely be fine, but this hasn't been stated for the post-cutover connection string).

## 3. `CREATE USER ... FROM EXTERNAL PROVIDER`

**Verified current.** Confirmed via [`CREATE USER (Transact-SQL)`](https://learn.microsoft.com/sql/t-sql/statements/create-user-transact-sql), ["Configure and manage Microsoft Entra authentication with Azure SQL"](https://learn.microsoft.com/azure/azure-sql/database/authentication-aad-configure), and the general Entra-for-Azure-SQL overview doc. This is still the correct, current, unchanged T-SQL syntax for creating a contained database user backed by any Microsoft Entra principal — user, group, service principal, or managed identity (managed identities are provisioned by their display name, same as a service principal: `CREATE USER [appName] FROM EXTERNAL PROVIDER;`). No finding.

## 4. No native Bicep/ARM resource for a database-level user

**Verified accurate — still true.** Searched the current `Microsoft.Sql` resource-provider template reference (all versions) for anything resembling a database-user or database-principal child resource; none exists. The only Entra-related `Microsoft.Sql` resources are at the **server** level (`administrators`, `azureADOnlyAuthentications`) — there is still no `Microsoft.Sql/servers/databases/users`-shaped resource, and no first-party Bicep/ARM primitive for running arbitrary T-SQL (the closest generic tool, `Microsoft.Resources/deploymentScripts`, is what AD-21 explicitly and correctly rules out as the alternative it's choosing not to use). Manual T-SQL remains the only path today. No finding.

## 5. `Microsoft.Sql/servers` `administrators` block and `Microsoft.Sql/servers/azureADOnlyAuthentications` (name: `'Default'`)

**Verified current.** The `Microsoft.Sql/servers/azureADOnlyAuthentications` child-resource type is confirmed live across API versions from `2020-02-02-preview` through at least `2025-01-01` (non-preview), with `name` required to be the literal string `'Default'` and a single `properties.azureADOnlyAuthentication: bool`. The inline `administrators` block on `Microsoft.Sql/servers` (Entra Admin configuration) is likewise current and the commonly-documented alternative. Both options AD-21 names are real and current. No finding.

## 6. `Microsoft.Data.SqlClient` 6.1.1 / `Azure.Identity` 1.14.2, and the 7.0 packaging split

**Verified accurate, with one framing nuance worth tightening (low severity).**

- The 7.0 split is real and confirmed directly from the official `Microsoft.Data.SqlClient` 7.0 release notes: *"Starting with Microsoft.Data.SqlClient 7.0.0, Microsoft Entra authentication support is provided through the separate Microsoft.Data.SqlClient.Extensions.Azure package. The core driver package no longer carries Azure dependencies."* Confirmed the `Microsoft.Data.SqlClient.Extensions.Azure` package exists on NuGet (current release 7.0.2) and migration requires *only* adding that package reference — "No code changes are required beyond adding the package reference," per Microsoft's own migration guide. `Microsoft.Data.SqlClient` 6.1.1 (the version this repo's lockfile resolves to, confirmed in-repo and not re-checked here) predates this split and still bundles `Azure.Identity` transitively, so AD-21's "zero new NuGet references work today" claim holds.
- **Nuance:** as of this review date, `Microsoft.Data.SqlClient` 7.0 is **already GA** (Microsoft's own blog post is titled "Microsoft.Data.SqlClient 7.0 Is Here"), not a hypothetical future release — AD-21's phrasing ("Watch item for a future ... bump") reads as if 7.0 is still upcoming. It isn't; it has shipped, and the migration path is a single added package reference with no code change, which is good news but worth stating accurately (it's an available, low-effort upgrade today, not a distant unknown). This doesn't change the substance of the watch item — 6.1.1 is still what's pinned and still works — but the "future" framing slightly understates how close this is.
- One additional minor inconsistency worth flagging for awareness (not AD-21's fault): Microsoft's own docs disagree with each other on exactly which version introduced the split — the SqlClient release notes attribute it to 7.0.0, while a separate ASP.NET Core breaking-changes article states *"Starting in Microsoft.Data.SqlClient 6.0, the Microsoft Entra ID ... authentication providers are no longer in the main package."* AD-21 follows the more authoritative source (the SqlClient project's own release notes), which is the right call, but the discrepancy exists in Microsoft's own documentation.

---

## Summary of findings

| # | Item | Verdict | Severity |
|---|---|---|---|
| 1 | `Active Directory Managed Identity` keyword | Confirmed current | — |
| 2 | `Active Directory Default` / `AzureCliCredential` fallback works, but AD-21 omits documented CI-runner latency characteristic | Confirmed correct but incomplete | **Medium** |
| 3 | `CREATE USER ... FROM EXTERNAL PROVIDER` | Confirmed current | — |
| 4 | No native Bicep/ARM resource for a DB-level user | Confirmed still true | — |
| 5 | `administrators` block / `azureADOnlyAuthentications` (`'Default'`) | Confirmed current | — |
| 6 | SqlClient 6.1.1 + Azure.Identity 1.14.2, 7.0 split to `Extensions.Azure` | Confirmed accurate; "future" framing slightly understates that 7.0 is already GA | **Low** |

No claim in AD-21 was found to be hallucinated, deprecated, or contradicted by current Microsoft documentation. The AD reads as genuinely researched rather than asserted from training data — every keyword, resource type, and package name checks out against live sources. The two findings above are refinements (an operational-latency callout, and tightening "future" language now that SqlClient 7.0 has shipped), not corrections of factual errors.

## Other spine observations (not part of AD-21, flagged per reviewer instructions)

Nothing else in the linked spine files (`design-paradigm.md`, `consistency-conventions.md`, `stack.md`, `structural-seed.md`, `capability-architecture-map.md`, `deferred.md`) was in scope for deep verification this pass, but nothing encountered while cross-referencing AD-21 (e.g. `stack.md`'s Azure SQL Basic-tier description, `structural-seed.md`'s deployment diagram) appeared stale or contradicted by this research.
