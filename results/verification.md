# Verification

The current verified state of this repository. Earlier dated runs are kept, unchanged in substance,
under [`history/`](history/).

## Environment

| Item | Value |
|---|---|
| Date | 2026-10-04, Europe/Istanbul |
| Source commit | `bdd6354` — see [Commit and CI](#commit-and-ci) |
| .NET SDK | `10.0.400` (pinned in `global.json`, `rollForward: disable`); runtime `Microsoft.AspNetCore.App 10.0.11` |
| Local machine | Windows 11 Pro 10.0.26200, Docker Desktop 29.6.1 (Linux containers) |
| CI | GitHub Actions, `ubuntu-latest`, [`.github/workflows/ci.yml`](../.github/workflows/ci.yml) |
| SQL Server | `mcr.microsoft.com/mssql/server@sha256:97b448857967be55e005424a660056fe6d51814435804dc07e8f79f028bab5fb` (2022), the same digest in `compose.yaml` and `SqlServerFixture` |
| Test stack | xUnit v3 on Microsoft.Testing.Platform, Testcontainers; no SQLite or in-memory substitute |

## Commands

The same commands, in the same order, run locally and in CI:

```
dotnet tool restore                                      dotnet-ef 10.0.11 restored
dotnet restore --locked-mode                             all four projects restored
dotnet build --no-restore --configuration Release        0 warnings, 0 errors (warnings are errors)
dotnet format --verify-no-changes --no-restore           exit 0
dotnet test --no-build --configuration Release -- --report-trx --report-trx-filename local.trx
                                                         116 total / 116 passed / 0 failed / 0 skipped
```

TRX counters: `total="116" executed="116" passed="116" failed="0"`.

## Test result by suite

| Suite | Passed | What it covers |
|---|---|---|
| `OrderTests` | 22 | Company-specific and list pricing, price snapshots, validation, Buyer/Viewer roles, stable paging, line order (`GetReturnsTheLinesInTheOrderTheyWereSubmitted`) |
| `ErrorContractTests` | 17 | `application/problem+json` with `errorCode` and `traceId` on every failure path, regardless of `Accept`; no internal detail leaked |
| `MigrationCompatibilityTests` | 17 | S1 → S2 → S2B → S3 with real V1, V2 and V3 processes; backfill gate; contract and rollback behaviour |
| `AuthenticationTests` | 16 | Signature, expiry, issuer and audience validation; token-only identity, spoofed headers ignored; inactive user, membership or company refused; database role beats a token role claim |
| `AuthenticationStartupTests` | 11 | The local demo auth mode refuses to start outside Development and Testing, through every key path |
| `IdempotencyTests` | 11 | Replay, payload conflict, ten concurrent identical creates → one order, failure before commit, lock budget → `503` |
| `IsolationTests` | 7 | Company A cannot read or list company B orders; a shared member sees only the selected company; tenant queries without context fail; cross-tenant writes and cross-tenant line foreign keys are rejected |
| `OpenApiTests` | 5 | Headers, error schemas, Bearer scheme, anonymous `/health` |
| `SchemaConstraintTests` | 5 | Foreign keys reject orders, prices and idempotency records for unknown companies or users |
| `ModuleBoundaryTests` | 3 | Ordering reaches Access only through its contract (IL-level check) |
| `LineNumberMigrationTests` | 1 | Upgrading S4 → S5 numbers existing multi-line orders 1, 2, 3 and the unique index refuses a duplicate |
| `HealthTests` | 1 | `/health` is anonymous and healthy |
| **Total** | **116** | |

## Migration coverage

All six migrations are exercised against real SQL Server:

- **S1 → S2 → S2B → S3** — the expand/backfill/contract experiment, with old and new code running as
  separate processes on one database. Details and per-test observations in
  [`migration.md`](migration.md).
- **S4** — ownership foreign keys, proven by `SchemaConstraintTests`, which write through a path that
  bypasses the application guard.
- **S5** — `OrderLines.LineNumber`, an explicit 1-based submission position with a unique
  `(CompanyId, OrderId, LineNumber)` index and `CHECK ([LineNumber] > 0)`. Reads sort by it instead
  of by the version 7 Guid id, which SQL Server does not sort chronologically. The backfill and
  its failure mode are covered by `LineNumberMigrationTests`; the background is in
  [`history/2026-10-04-line-ordering-fix.md`](history/2026-10-04-line-ordering-fix.md).

## Commit and CI

| Item | Value |
|---|---|
| Verified source commit | `bdd6354aac957423d85575a57a37e6a6407dec49` (`main`) |
| Local run | the commands above, in the working tree: 116/116 |
| Fresh clone | `git clone` from GitHub into a short path, then the same five commands: 0 warnings, format clean, **116/116** |
| GitHub Actions | [ci run 37213428789](https://github.com/hidayetcolkusu/dotnet-b2b-ordering-case-study/actions/runs/37213428789) on `bdd6354`: **success**, log reports `total: 116, failed: 0, succeeded: 116` |

Commits after `bdd6354` change only this file; each runs the same workflow, and the badge in the
README shows the result for the current `main`.

## Known verification limits

- **Persistence compatibility, not zero downtime.** The migration experiment runs real processes
  against one database, but under no live traffic.
- **No load or soak testing.** The concurrency test is ten simultaneous requests, which proves the
  unique-index and key-lock logic, not throughput.
- **Local authentication only.** Tokens come from `dotnet user-jwts`; no identity provider was
  integrated or tested.
- **Windows deep paths.** On Windows, a checkout path long enough to push the native SNI library past
  `MAX_PATH` makes every `MigrationCompatibilityTests` case fail; see the README setup notes. CI runs
  on Linux, where this does not apply.
- **The HTTP smoke checks and Swagger checks against a running API** were last run by hand on
  2026-09-10 and 2026-09-12 (see [`history/`](history/)). The same contract is asserted in the
  suite by `ErrorContractTests`, `OpenApiTests` and the order tests.

## History

| Date | Record | Summary |
|---|---|---|
| 2026-09-10 | [Initial verification](history/2026-09-10-initial-verification.md) | First complete run (96 tests), manual setup and HTTP smoke checks, S2B and S4 added |
| 2026-09-12 – 13 | [Hardening and first CI runs](history/2026-09-12-hardening-and-ci.md) | Startup refusal for demo auth, backfill agreement check, clean-copy setup, first green CI (114 tests) |
| 2026-10-04 | [Order line ordering](history/2026-10-04-line-ordering-fix.md) | `LineNumber` and S5, regression and backfill tests (116 tests) |

Commit ids quoted in the history records belong to the pre-release history, which was
consolidated into a single public baseline commit; they do not resolve in this repository.

## Secret hygiene

`.env` is git-ignored and holds a locally generated password. The connection string lives in
dotnet user-secrets, outside the repository. No token, password or connection string appears in
these files or in script output.
