# Hardening and first CI runs — 2026-09-12 to 2026-09-13

Historical record. The current verified state is in [`../verification.md`](../verification.md).

Checking the implementation against its own plan turned up five items: two real defects, two
evidence gaps and a set of documentation claims that did not match the code. This record covers
the work done on them, labelled G1–G5, and the commands that were actually run for it.

It does not restate the 2026-09-10 numbers in
[`2026-09-10-initial-verification.md`](2026-09-10-initial-verification.md); where the two disagree,
this file is the later run.

When the first sections were written the repository had no commits, so they are not pinned to a
commit id; see [the closing note](#status-at-first-commit--2026-09-13). The commit ids that do
appear (`3111698`, `b06e5c5`) belong to the pre-release history, which was later consolidated into
the public baseline commit, so they no longer resolve in this repository. The dated sections are
otherwise left as they were recorded.

## Environment

| Item | Value |
|---|---|
| Date | 2026-09-12, Europe/Istanbul |
| Source state | working tree, untracked (no commit exists) |
| SDK | `dotnet --version` → `10.0.400` |
| Docker | `docker info --format '{{.ServerVersion}}'` → `29.6.1` (available this time) |
| SQL Server | `mcr.microsoft.com/mssql/server@sha256:97b4488…ab5fb`, the same digest in `compose.yaml` and `SqlServerFixture` |

## Commands and results

```
dotnet format --verify-no-changes --no-restore          exit 0
dotnet build --no-restore --configuration Release       0 warnings, 0 errors
dotnet test --no-build --configuration Release -- --report-trx --report-trx-filename remediation.trx
                                                        114 total / 114 passed / 0 failed / 0 skipped
```

TRX counters agree: `total="114" executed="114" passed="114" failed="0"`, written to
`TestResults/remediation.trx`.

The suite was 96 tests on 2026-09-10 and is 114 now: eleven in the new `AuthenticationStartupTests`,
five new migration cases, and two theory cases in `OrderTests`.

## What was fixed, and the failing test that came first

### G1 — demo authentication now refuses to start outside Development/Testing

`AuthenticationSetup.AddApiAuthentication` refused startup only when `Jwt:SigningKey` was populated.
Nothing covered the framework's own `Authentication:Schemes:Bearer` path (where `dotnet user-jwts`
writes its key), and nothing covered the case where no key is configured at all and the built-in
demo issuer and audience simply stay in effect.

The mode is now named in code (`AuthenticationSetup.DemoMode` = `LocalSymmetricDemo`) and the
refusal is unconditional outside Development and Testing. The message names which configuration
paths carried key material, never their values.

**Before:** with the old guard restored, `AuthenticationStartupTests` failed 6 of 11 —

```
TheRealHostRefusesToStart(environment: "Production")   Assert.ThrowsAny() Failure: No exception was thrown
TheRealHostRefusesToStart(environment: "Staging")      Assert.ThrowsAny() Failure: No exception was thrown
StartupIsRefusedOutsideDevelopmentAndTesting(Production, demo-defaults)  No exception was thrown
StartupIsRefusedOutsideDevelopmentAndTesting(Production, user-jwts)      No exception was thrown
StartupIsRefusedOutsideDevelopmentAndTesting(Staging, demo-defaults)     No exception was thrown
StartupIsRefusedOutsideDevelopmentAndTesting(Staging, user-jwts)         No exception was thrown
```

**After:** 11 / 11 pass.

```
dotnet test --no-build --configuration Release --filter-class "*AuthenticationStartupTests"
dotnet test --no-build --configuration Release --filter-class "*AuthenticationTests"
```

Changed: `src/B2B.Ordering.Api/Shared/Auth/AuthenticationSetup.cs` (`DemoMode`,
`IsDemoModeSupported`, `DescribeSigningKeySources`), new
`tests/B2B.Ordering.Tests/AuthenticationStartupTests.cs`, ADR 0002, README, code walkthrough.

Scope note: this is a startup contract. It is not a claim that an unverified token was previously
accepted or that tenant data was reachable — nothing was tested about that, and nothing should be
said about it.

### G2 — the S2B backfill step now verifies agreement before V3 may start

S2B copied values and stopped there. A row holding an old and a new value that differ was not
touched by the copy (it fills nulls only), so the gate reported success and the disagreement
surfaced much later, at S3.

S2B now copies and then checks, and S3 shares the same predicate rather than restating it. Two
decisions are explicit: a row V3 wrote (new column only, old one null) is correct and not a
mismatch, and the comparison is forced to `Latin1_General_BIN2` so a case difference counts as a
difference instead of being absorbed by the database's case-insensitive default collation. The
operator's repeat path (`MigrationRunner.BackfillSql`) runs the same two statements inside a
transaction, so a failed check rolls back the copy and leaves both columns in place.

**Before:** with the agreement check removed, 3 of 17 migration tests failed —

```
BackfillGateIsRefusedWhenTheColumnsDisagree          Assert.ThrowsAny() Failure: No exception was thrown
RepeatedBackfillIsRefusedWhenTheColumnsDisagree      Assert.ThrowsAny() Failure: No exception was thrown
BackfillGateTreatsACaseDifferenceAsADisagreement     Assert.ThrowsAny() Failure: No exception was thrown
```

**After:** 17 / 17 pass on real SQL Server.

```
dotnet test --no-build --configuration Release --filter-class "*MigrationCompatibilityTests"
```

Changed: `Migrations/20260910193934_S2B_BackfillExternalReference.cs` (`CopySql`,
`AgreementCheckSql`), `Migrations/20260910193938_S3_ContractDropCustomerReference.cs`,
`tests/.../Support/MigrationRunner.cs`, ADR 0005, migration-compatibility page.

### G3 — the empty-reference scenario is now covered

`NullReferenceSurvives` covered null; nothing covered a genuinely empty string.
`EmptyReferenceSurvives` passes a real empty command-line argument to the V1 helper and follows the
value through S2, the backfill gate and S3, asserting at every step that it is `''` and not null.

This is a persistence question only. `OrderTests.NormalizesBlankExternalReferenceToNull` asserts the
separate API decision — V3 normalises a blank `externalReference` to null on both create and read —
so the two are not confused.

Both passed on the first run after being written: the existing code already carried the empty
string correctly, and the gap was in the evidence, not in the behaviour. Saying otherwise would
overstate the fix.

### G4 — three documentation claims corrected

| Claim | Correction |
|---|---|
| ADR 0005: after V3 writes the new column only, "V1 and V2 would read null" | Only V1 does. V2 reads the new column first. `RollbackToV2RequiresReverseBackfill` now asserts both halves before it synchronises anything, and the ADR explains why the reverse backfill is still required — for the old column, V1 compatibility and the S2B/S3 gate. |
| ADR 0001 (Türkçe): separate assemblies "lose the single transaction and single migration set" | They do not; separate *services* and separate databases would. The real cost is project topology, a third shared assembly, and version management. |
| ADR 0001 / architecture overview: the shared exception described only as `AppDbContext` and `SeedData` | The boundary test also exempts the `Shared.Persistence.Configurations` namespace. All three are now listed, with the mapping-only exemption stated as such. |

`ModuleBoundaryTests` (3/3) and `MigrationCompatibilityTests` (17/17) pass, and the anchors added
to the cross-links (`#the-infrastructure-exception-in-full`,
`#the-one-supported-authentication-mode`) match real headings.

### G5 — clean setup from a separate disposable source copy

A disposable copy was made carrying source, lock and tool manifests only: **114 files**, with
`.git`, `.env`, `bin`, `obj`, `results` and `TestResults` all verified absent from the copy. It was
driven with `APPDATA` redirected to an empty directory, so the machine's existing user-secrets store
for `UserSecretsId b2b-ordering-case-study` was not visible to it, and against its own SQL Server
container on its own port and volume. The development database was never touched — the development
container stayed stopped throughout, and the only container running was the probe.

```
dotnet tool restore                                     dotnet-ef 10.0.11 restored
dotnet restore --locked-mode                            all four projects restored
dotnet build --no-restore --configuration Release        0 warnings, 0 errors
dotnet format --verify-no-changes --no-restore           exit 0
dotnet test --no-build --configuration Release -- --report-trx --report-trx-filename clean-copy.trx
                                                        114 total / 114 passed / 0 failed
```

Then the README flow, end to end, on the clean copy:

```
docker compose -p b2b-clean-probe up -d                 (MSSQL_CONTAINER_NAME/MSSQL_HOST_PORT overridden)
pwsh -File scripts/init-dev.ps1 -ContainerName b2b-clean-probe-mssql -Port 14331 -Database B2BOrderingCleanProbe
dotnet user-jwts create ... --claim app_user_id=<seeded id>     (into the isolated secret store)
```

`init-dev.ps1` applied S1, S2, S2B, S3 and S4 in order on an empty database and seeded it. HTTP
results against the running API:

| Probe | Result |
|---|---|
| `GET /health` | 200 |
| Buyer creates an order (company A) | 201, total 160 TRY |
| Buyer reads it back | 200 |
| Same key, same body | 201, same order id |
| Same key, different body | 409 (the idempotency key conflict; the status was captured, the body's `errorCode` was not read back in this probe) |
| Viewer lists orders | 200, `totalCount` 1 |
| Viewer creates an order | 403, `errorCode: role_not_allowed` |
| Company A token with `X-Company-Id: B` | 403 |
| Company B member asking for company A's order id | 404, `errorCode: order_not_found`, `application/problem+json` |
| No token | 401 |

Finally the migration demo, on its own disposable database inside the probe container:

```
pwsh -File scripts/migration-demo.ps1 -Configuration Release -ContainerName b2b-clean-probe-mssql -Port 14331
```

It walked S1 → S2 → backfill → S3 with the real V1 and V2 executables, ended with V1's expected
controlled failure (`Invalid column name 'CustomerReference'`), and dropped its database.
`docker compose -p b2b-clean-probe down -v` removed the container, network and volume; the
development container and its volume were left as they were.

**Hidden-state dependency found:** two setup scripts and `compose.yaml` hard-coded the container
name, host port and database name, so a second instance could not be brought up beside the
development one without colliding with it. The minimum fix was applied: `compose.yaml` reads
`MSSQL_CONTAINER_NAME` and `MSSQL_HOST_PORT` with the development values as defaults, and
`init-dev.ps1` and `migration-demo.ps1` take `-ContainerName`, `-Port` and (for `init-dev.ps1`)
`-Database`, again defaulting to the development values. `docker compose up -d` and both scripts
behave exactly as before when the parameters are omitted.

No other hidden-state dependency appeared: the clean copy built, formatted and passed the full suite
without a `.env`, without prior build output and without access to the machine's secret store.

## What is still not proven

- **Remote CI has not been run.** `.github/workflows/ci.yml` exists and the Linux job is defined;
  no run of it is being claimed here. Package-source and restore behaviour on a machine without a
  warm NuGet cache was not tested either — the clean copy restored from the local global package
  folder.
- **No commit or tag existed yet**, so nothing in this run is pinned to one.
- **Zero downtime is still not demonstrated.** The migration experiment proves persistence
  compatibility between real V1, V2 and V3 processes on real SQL Server, under no live traffic.
- **The README flow above was driven by a script, not by hand.** The commands are the README's
  commands and the responses are real, but a human typing them is a different test of the
  instructions' clarity.

---

# CI readiness check — 2026-09-13

A follow-up run, aimed at the one item the 2026-09-12 record listed as unproven: whether the CI
workflow would actually pass. It still has **not** been run remotely — the repository has no commits,
so there is nothing to push and nothing for GitHub Actions to pick up. What follows is the closest
commit-free substitute, plus the defects it found.

## What git would actually publish

Nothing had ever been committed, so the contents of a fresh clone had never been checked. The file
set `git add -A` would stage was materialised into a separate directory and used as a stand-in for
a clone:

```
git ls-files --others --exclude-standard        117 files
```

`.git`, `.env`, `bin/`, `obj/` and `TestResults/` are absent from it; the four `packages.lock.json`
files, `.config/dotnet-tools.json`, `global.json`, `Directory.*.props`, the `.slnx` and
`.github/workflows/ci.yml` are all present (`git check-ignore` confirms none of the lock files is
ignored). The CI steps were then run against that file set, in the workflow's own order:

```
dotnet tool restore                                      dotnet-ef 10.0.11 restored
dotnet restore --locked-mode                             all four projects restored
dotnet build --no-restore --configuration Release        0 warnings, 0 errors
dotnet format --verify-no-changes --no-restore           exit 0
dotnet test --no-build --configuration Release -- --report-trx --report-trx-filename ci.trx
                                                         114 total / 114 passed / 0 failed
```

So the committed file set is complete: a fresh clone builds and passes without anything that only
exists on this machine.

## A real trap found on the way: deep Windows paths

The first attempt at the run above failed — **15 of 114**, every one of them in
`MigrationCompatibilityTests`, with the helper processes reporting:

```
{"version":"V1","error":"The type initializer for 'Microsoft.Data.SqlClient.TdsParser' threw an exception."}
```

The cause is not in the repository. `Microsoft.Data.SqlClient` loads its native SNI library from
`bin/.../runtimes/win-arm64/native/Microsoft.Data.SqlClient.SNI.dll`, and that path crossed
Windows' 260-character `MAX_PATH` limit in the deep temporary directory the first copy lived in.
The same binary, byte-for-byte identical (60 files, no diff against the working tree's output), ran
correctly when copied to `C:\Temp\`, and the whole suite went 114/114 from the short path.

It is worth recording rather than discarding, because the symptom names neither paths nor
`MAX_PATH`: a developer cloning into a deep folder on Windows sees only these fifteen tests go red.
The README now warns about it under setup. Linux and macOS have no such limit, so GitHub's
`ubuntu-latest` runner is unaffected — this was not a prediction of CI failure.

## Two workflow defects fixed

Both were found by inspection against what the simulated run actually produced, not by guessing.

| Defect | Fix |
|---|---|
| The artifact step used `path: '**/TestResults/*.trx'` with `if-no-files-found: error`. Microsoft.Testing.Platform writes the report to `<repo>/TestResults/` — at the **root** — so the upload depended on `**/` matching zero directories. If it does not, a fully green run still fails at the last step. | The root pattern is now listed explicitly alongside the recursive one. |
| The workflow declared no `permissions`, so it inherited the repository default token scope for a job that only reads. | `permissions: contents: read` added at workflow level. |

The workflow still parses as valid YAML (8 steps).

## `.gitattributes` added

There was none, so committed line endings depended on whoever ran `git add` and their local
`core.autocrlf` (`true` on this machine, which would normalise to LF — but that is a property of
this machine, not of the repository). `* text=auto` makes the checkout identical on the Linux runner
and on a Windows developer machine regardless of local git configuration, `*.sh text eol=lf` keeps
shebang lines runnable, and a few asset types are marked binary. This is the same class of problem
the clean-setup experiment exists to find: behaviour that depends on hidden machine state.

## Still not done

- **The remote CI run itself.** It needs a commit and a push; neither was made. `origin` is
  configured (`github.com/hidayetcolkusu/dotnet-b2b-ordering-case-study`) and the branch is `main`,
  but the repository is still empty of commits. Nothing here may be presented as "CI passed".
- **Cold-cache restore.** Both clean runs resolved packages from this machine's global NuGet folder.
  A runner with an empty cache downloads from nuget.org; that path is untested.


---

# Status at first commit — 2026-09-13

The two sections above were written while the repository had no commits, and several of their
closing notes say so. Those notes were accurate when recorded and are deliberately left in place.
This section supersedes them.

| Item | Then | Now |
|---|---|---|
| Commit | none existed | `3111698` (`3111698360bcf2b4593635b15c3d1da3504bd4ee`), branch `main`, working tree clean |
| Published file set | predicted from `git ls-files --others --exclude-standard` | **114 files** actually committed, matching the prediction |
| Remote | `origin` configured, nothing to push | pushed; `HEAD` and `origin/main` are the same commit |

**The CI run is still not reported here.** The workflow can now be triggered by the push, but this
file makes no claim about its result: the run's outcome belongs in the Actions tab, not in a
document that cannot see it. Three things differ between the local simulation that went 114/114 and
the hosted runner, and any of them can change the answer:

- Linux rather than Windows, which removes the `MAX_PATH` trap described above but exercises paths,
  file-name casing and line endings that were never run on Linux here.
- A cold NuGet cache. Both clean runs resolved from this machine's global package folder; the runner
  downloads from nuget.org under `--locked-mode`.
- Testcontainers pulling the pinned SQL Server image (~1.5 GB) inside the runner.

Read the run, then record its real outcome — pass or fail — as its own dated entry. Do not present
this file as evidence that CI passed.

---

# CI run — 2026-09-13

The workflow has now run on GitHub Actions. Read from the repository's Actions tab:

| Run | Commit | Branch | Conclusion | Duration |
|---|---|---|---|---|
| `ci #1` | `3111698` | `main` | success | 2m 43s |
| `ci #2` | `b06e5c5` | `main` | success | 2m 43s |

Both runs are green, which closes the last item the sections above left open. Because any failing
step fails the job, a green run means every step passed on `ubuntu-latest`:

- `dotnet tool restore` and `dotnet restore --locked-mode` against a **cold NuGet cache**. This was
  listed as untested twice above — both local clean runs resolved from this machine's global package
  folder. It is now exercised: the lock files are sufficient to restore from nuget.org.
- `dotnet build` and `dotnet format --verify-no-changes` **on Linux**, which is what the
  `.gitattributes` normalisation was for. The blobs are stored with LF, and the format check agreed.
  File-name casing, which only matters on a case-sensitive filesystem, is also now exercised.
- The full test suite, with Testcontainers pulling the pinned SQL Server image inside the runner.
  The `MAX_PATH` trap that broke fifteen migration tests locally does not exist on Linux, as
  expected.
- The artifact upload, which is worth calling out: it runs with `if-no-files-found: error`, so a run
  that produced no matching `.trx` would have failed at the last step even with every test passing.
  It did not, which confirms the root-`TestResults/` pattern added on 2026-09-13 matches what
  Microsoft.Testing.Platform actually writes.

## What this does and does not say

It says the workflow passes on a clean hosted runner from a fresh checkout of these two commits.

It does not restate a test count: the per-test numbers in the run log were not read here, only the
run conclusion. The local figure of 114/114 stands on the local runs recorded above, not on this.

It also does not change any of the standing limitations — no zero-downtime claim, no identity
provider, no public deployment. A green CI badge is a statement about the build, not about the
system being production ready.
