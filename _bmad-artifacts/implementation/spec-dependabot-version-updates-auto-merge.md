---
title: 'Dependabot version updates with auto-merge for patch and minor updates'
type: 'chore'
created: '2026-10-11'
status: 'in-review'
review_loop_iteration: 0
context: ['docs/codebase-audit-2026-10-10.md (S11)']
baseline_commit: '5f55fa6f475a40ca1cb733023422a8275848ab34'
---

<frozen-after-approval reason="human-owned intent — do not modify unless human renegotiates">

## Intent

**Problem:** The repo has no `.github/dependabot.yml` (audit S11). Only Dependabot security updates are enabled in the repo settings, so routine version updates never arrive and the open security PRs (#97–#100) each need a manual merge.

**Approach:** Add a `dependabot.yml` for npm (`/web`), NuGet, GitHub Actions and Docker with weekly grouped patch/minor PRs and a 3-day cooldown. Add `dependabot-automerge.yml`, which enables GitHub auto-merge (squash) on Dependabot PRs that are patch or minor updates. Branch protection (`build-test-lint`, `validate-infra`, `strict: true`) stays unchanged and decides when the merge actually happens.

## Boundaries & Constraints

**Always:** Auto-merge is enabled only for `version-update:semver-patch` and `version-update:semver-minor`, or for PRs of the minor/patch groups defined in `dependabot.yml`. Only when `github.actor == 'dependabot[bot]'`. Merge method is squash, matching the repo history.

**Ask First:** Any widening of the allowed update types (majors), any change to the required checks or `strict`, any use of a PAT or GitHub App token in these workflows.

**Never:** No auto-merge of major updates. No automatic rebase or `update-branch` workflow: PRs that fall behind `main` are rebased by hand with `@dependabot rebase` (decision 2026-10-11, Ralf; the default `GITHUB_TOKEN` would not retrigger the required checks, and a PAT was declined). No relaxing of `strict: true`.

## I/O & Edge-Case Matrix

| Scenario | Input / State | Expected Output / Behavior | Error Handling |
|----------|--------------|---------------------------|----------------|
| Weekly grouped minor/patch PR, checks green, up to date | Dependabot PR, `dependency-group` set | Auto-merge enabled, squash-merged by GitHub after the checks pass | N/A |
| Same PR, checks fail | Red `build-test-lint` | Auto-merge stays pending, nothing merges | PR stays open for manual fix |
| Same PR, `main` moved on | PR behind `main` (`strict`) | Not merged, waits | Rebase by hand: `@dependabot rebase` |
| Major update PR | `update-type` = `version-update:semver-major` | Workflow logs a notice, auto-merge not enabled | Manual review and merge |
| Security update PR (ungrouped) | patch/minor `update-type` | Auto-merge enabled like any patch/minor | Same as above |
| Human pushes to a Dependabot branch | `github.actor` is not Dependabot | Job skipped, auto-merge not (re-)enabled | N/A |

</frozen-after-approval>

## Code Map

- `.github/dependabot.yml` -- version-update config, four ecosystems, grouped minor/patch, 3-day cooldown.
- `.github/workflows/dependabot-automerge.yml` -- metadata via `dependabot/fetch-metadata@v2`, then `gh pr merge --auto --squash`.
- `infra/README.md` -- new section "Dependabot version updates and auto-merge".

## Known Risks

- Merging to `main` deploys to production (`app-deploy.yml`), so the test suite is the only gate for auto-merged updates. Cooldown reduces, but does not remove, the supply-chain exposure.
- `OpenTelemetry.Instrumentation.EntityFrameworkCore` is a prerelease (`1.17.0-beta.1`); how `fetch-metadata` classifies a prerelease bump is unverified. Watch the first NuGet PR.
- Not verifiable before merge: Dependabot and `pull_request` workflows run from the PR's base branch config, so the first real check happens after this lands on `main` and the next Dependabot PR opens.

## Verification

- YAML of both files parses. After merge: an existing Dependabot PR (rebased or reopened) shows the "Dependabot Auto-merge" run and "Auto-merge enabled" for patch/minor; a major PR shows the notice and no auto-merge.
