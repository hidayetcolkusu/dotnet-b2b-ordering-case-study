# Migration compatibility results

**SDK** 10.0.400 · **dotnet-ef** 10.0.11 · **SQL Server 2022** container pinned by digest
`sha256:97b4…b5fb`. The automated results below are from the current verified run (full
environment in [`verification.md`](verification.md)); the manual demo output is from 2026-09-10.

## Automated run

```
dotnet test --no-build --configuration Release --filter-class "*MigrationCompatibilityTests"
```

Part of the full suite run: **17 tests, 17 passed, 0 failed.** Each test
used its own freshly created database inside the shared container, seeded with the same synthetic
companies and users as the rest of the suite (the `Orders` table has real foreign keys onto them).

| Test | Observed |
|---|---|
| `V1WorksOnS1AndS2` | V1 wrote and read on S1; after the expand it still read the old row and wrote a new one |
| `V2ReadsV1BeforeBackfill` | V2 read a row V1 wrote after the expand, `source: CustomerReference` (the fallback path) |
| `V1ReadsV2OnS2` | V1 read a row V2 wrote, `source: CustomerReference` — two separate processes, one database |
| `LateV1WriteIsInvisibleToV3UntilTheBackfillStep` | After the expand, a V1 row read as `null` from the new column; the `S2B` backfill step made it visible, and V2 then read it from `ExternalReference` |
| `BackfillStepIsRepeatable` | Running the backfill statement twice more, after a straggling V1 write, was safe and picked the row up |
| `BackfillGateIsRefusedWhenTheColumnsDisagree` | With both columns populated but different, the S2B gate threw `Backfill mismatch` instead of reporting success |
| `RepeatedBackfillIsRefusedWhenTheColumnsDisagree` | The operator's repeat path runs copy and check in one transaction; a mismatch rolled the copy back and left both columns in place |
| `BackfillGateTreatsACaseDifferenceAsADisagreement` | `PO-Case` vs `po-case` counted as a mismatch: the comparison is forced to `Latin1_General_BIN2` rather than the database's case-insensitive default |
| `BackfillGateAcceptsTheStatesThatAreNotDisagreements` | Rows the migration legitimately produces — including a V3 row with the new column only and the old one null — passed the gate |
| `EmptyReferenceSurvives` | A genuinely empty string stayed `''`, not null, through S2, the backfill gate and S3 |
| `ContractIsRefusedWhenTheColumnsStillDisagree` | With the two columns deliberately out of sync, the contract migration threw `Backfill mismatch` and left `CustomerReference` in place |
| `LateV1WriteIncludedInFinalBackfill` | A V1 row written just before shutdown had its value in `ExternalReference` after the contract migration |
| `NullReferenceSurvives` | A null reference stayed null through the fallback read and through the contract step |
| `V3WorksOnS2` | The final API created and re-read an order on the expanded schema, `externalReference` preserved |
| `V3WorksOnS3` | Same on the contracted schema |
| `ContractRejectsV1AndV2AsExpected` | After the drop, both helpers exited non-zero and their errors named `CustomerReference` — asserted, not left red |
| `RollbackToV2RequiresReverseBackfill` | A V3-style row (new column only) read as null from V1; after the reverse backfill both V1 and V2 read the value |

## Manual demo run

```
powershell -File scripts/migration-demo.ps1
```

Exit code 0. Output from the actual run, on a disposable database that was dropped afterwards:

```
Creating disposable database B2BOrderingMigrationDemo_e9aac6279fe5459694238490a5284f38

== S1: only CustomerReference exists ==
{"Version":"V1","Operation":"write","OrderId":"1dc109f3-…","Reference":"PO-S1","Source":"CustomerReference"}
{"Version":"V1","Operation":"read","OrderId":"1dc109f3-…","Reference":"PO-S1","Source":"CustomerReference"}

== S2 expand: both columns exist, V1 and V2 run side by side ==
{"Version":"V1","Operation":"write","OrderId":"858b0290-…","Reference":"PO-S2-FROM-V1","Source":"CustomerReference"}
V2 reads a V1 row through the fallback:
{"Version":"V2","Operation":"read","OrderId":"858b0290-…","Reference":"PO-S2-FROM-V1","Source":"CustomerReference"}
V1 reads a V2 row from the old column:
{"Version":"V2","Operation":"write","OrderId":"7e3643d4-…","Reference":"PO-S2-FROM-V2","Source":"both"}
{"Version":"V1","Operation":"read","OrderId":"7e3643d4-…","Reference":"PO-S2-FROM-V2","Source":"CustomerReference"}

== Late V1 write, then the separate backfill step ==
{"Version":"V1","Operation":"write","OrderId":"1ebb37c2-…","Reference":"PO-LATE-FROM-V1","Source":"CustomerReference"}
V2 still finds it only through the fallback, so V3 would see null:
{"Version":"V2","Operation":"read","OrderId":"1ebb37c2-…","Reference":"PO-LATE-FROM-V1","Source":"CustomerReference"}
Applying the backfill step (V1 and V2 writers are stopped at this point):
Now the value is in the new column, which is what V3 reads:
{"Version":"V2","Operation":"read","OrderId":"1ebb37c2-…","Reference":"PO-LATE-FROM-V1","Source":"ExternalReference"}

== S3 contract: backfill repeated as a guard, column dropped ==
V1 is now incompatible, which is the expected controlled failure:
  exit code 1: {"version":"V1","error":"Invalid column name 'CustomerReference'."}

Demo complete. The default development database was not modified.
Dropping B2BOrderingMigrationDemo_e9aac6279fe5459694238490a5284f38
```

The `source` field is the point of the middle block: before the backfill step the late V1 row is
reachable only through V2's fallback to the old column, so V3 — which reads only
`ExternalReference` — would see `null`. After `S2B` the same row reports
`source: ExternalReference`.

Order ids are abbreviated above; the run printed them in full. `B2BOrderingDev`, the default
development database, was verified untouched afterwards — it was the only `B2BOrdering*` database
left on the server.

## Migrations applied

`S1_Initial` → `S2_ExpandExternalReference` → `S2B_BackfillExternalReference` →
`S3_ContractDropCustomerReference`. Two later, unrelated migrations are not states of this
experiment: `S4_AddCompanyAndUserForeignKeys` adds the ownership constraints, and
`S5_AddOrderLineNumber` stores each order line's 1-based position, backfilling existing lines by
id under a unique `(CompanyId, OrderId, LineNumber)` index. `LineNumberMigrationTests` upgrades a
database holding a three-line order from S4 to S5 and asserts positions 1, 2, 3 and that the
index refuses a duplicate. `V3WorksOnS2` and `V3WorksOnS3` apply S5's operations first, because
today's API binary (V3) reads `LineNumber`.

EF's generated diff for S2 was a `RenameColumn`; it was replaced by hand with `AddColumn` plus a
re-runnable backfill.

`S2B` is the final backfill as its own step: the expand only copies the rows that exist when it
runs, so a V1 instance that keeps running writes rows V3 cannot see. `S2B` is the gate between
"the last old writer stopped" and "V3 may start". S3 then repeats the backfill, aborts with a
`THROW` if the two columns still disagree, and only then drops the old column.

A normal installation applies them all in order — `init-dev.ps1` did exactly that.

## What this proves, and what it does not

It proves persistence compatibility on a real SQL Server, with the old and new code running as
separate operating-system processes against the same database, and with the post-contract failure
asserted as the expected outcome.

It does **not** prove zero downtime under live traffic. It is not a gateway or authentication
compatibility test across versions. `samples/Migration.V1` and `samples/Migration.V2` are small
console helpers, not copies of the API. After the contract step a direct rollback to an old binary
is not supported: `Down()` recreates the column and copies values back, which is schema repair, not
data recovery.
