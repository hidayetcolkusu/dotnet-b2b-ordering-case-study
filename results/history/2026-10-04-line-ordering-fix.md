# Order line ordering — 2026-10-04

Order lines now carry an explicit 1-based `LineNumber`, so reads return them in submission order.
This record explains why sorting by the line id was wrong, how the fix was proven, and how the
migration treats rows that existed before it. The current verified state is in
[`../verification.md`](../verification.md).

## Environment

| Item | Value |
|---|---|
| Date | 2026-10-04, Europe/Istanbul |
| SDK | `dotnet --version` → `10.0.400` |
| Docker | `29.6.1` (Docker Desktop, WSL2) |
| SQL Server | `mcr.microsoft.com/mssql/server@sha256:97b4488…ab5fb`, the same digest as before |

## Defect: a read could return order lines in a different order than the create

`OrderProjection` sorted lines by `OrderLine.Id`, a version 7 Guid. SQL Server does not sort
`uniqueidentifier` from the first byte: it compares the last six bytes first, and in a version 7
Guid those bytes are random. So for an order with more than one line, `GET /api/orders/{id}`
returned the lines in a random order. That contradicted two things the repository says: line order
is part of the request's meaning (reordering lines is a different idempotency payload), and a
stored replay and a later GET return the same body.

No existing test created more than one line and then read it back, so the suite was green.

- **Reproduced first.** `OrderTests.GetReturnsTheLinesInTheOrderTheyWereSubmitted` creates five
  four-line orders and requires each GET body to equal its create body. Before the fix it failed
  on the first order: the create response started with `SKU-004` and the GET with `SKU-002`.
- **Fix.** `OrderLine.LineNumber` (1-based submission position) is written by the handler and used
  by the projection to sort lines. Migration `S5_AddOrderLineNumber` adds the column with a unique
  index on `(CompanyId, OrderId, LineNumber)` and a `CHECK ([LineNumber] > 0)`.
- **Existing rows.** S5 numbers lines that already exist by id, which is the order reads already
  gave them. Their original submission order was never stored and cannot be recovered; the
  migration says so rather than pretending otherwise.
- **The backfill is tested, and the test was checked by breaking it.**
  `LineNumberMigrationTests.ExistingMultiLineOrdersAreNumberedByTheBackfill` writes a three-line
  order at S4, migrates to S5 and expects positions 1, 2, 3, then expects the index to refuse a
  duplicate position. With the backfill statement removed, the test fails the way a real upgrade
  would: `CREATE UNIQUE INDEX ... duplicate key ... (…, 0)`.
- **Effect on the migration experiment.** `V3WorksOnS2` and `V3WorksOnS3` use today's API binary as
  V3, and that binary now reads `LineNumber`. Both went red on the first full run. They now apply
  S5's own operations on top of the experiment state first. S5 is additive and touches only
  `OrderLines`, so the reference-column question they ask is unchanged
  ([migration-compatibility.md](../../docs/architecture/migration-compatibility.md) records this).

## Documentation corrected in the same change

| Where | Said | Now says |
|---|---|---|
| ADR 0003, `results/idempotency.md` | the concurrency test asserts only one order; whether attempts replay "depends on timing" | the test asserts all ten return `201`. The losing attempts wait on the winner's key lock and then replay. The only timing-dependent outcome is going over the lock budget, which returns `503` |
| `Order` XML comment | immutability means "no late writer can race the backfill" | immutability rules out updates. New rows from a V1 instance still running after the expand are the race that S2B closes |
| `migration-compatibility.md`, `migration-demo.ps1` synopsis | demo walks S1 → S2 → S3 | S1 → S2 → S2B → S3, which is what the script does |

Also removed: `OrderJsonContext`, a source-generated JSON context that nothing used.

## Commands and results

```
dotnet restore --locked-mode                            exit 0
dotnet build --no-restore --configuration Release       0 warnings, 0 errors
dotnet format --verify-no-changes --no-restore          exit 0
dotnet test --no-build --configuration Release -- --report-trx --report-trx-filename local.trx
                                                        116 total / 116 passed / 0 failed / 0 skipped
```

TRX counters: `total="116" executed="116" passed="116" failed="0"`. The two new tests account for
114 → 116.

The same commands, starting with `dotnet tool restore`, were then run in a clean copy in a short
temporary path containing only the files `git ls-files -co --exclude-standard` lists (no
`bin`/`obj`). Every command exited 0 and the suite was again 116/116. GitHub Actions later ran the
same suite on `ubuntu-latest` and reported 116 passed, 0 failed.
