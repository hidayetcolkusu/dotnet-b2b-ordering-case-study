# Migration compatibility experiment

The full reasoning is in [ADR 0005](../decisions/0005-migration-compatibility.md). This page is the
map of what runs where.

## The three states

```
 S1                    S2 (expand)                S2B (backfill)         S3 (contract)
 ┌─────────────────┐   ┌──────────────────────┐   ┌─────────────────┐   ┌─────────────────┐
 │CustomerReference│ ─►│CustomerReference     │ ─►│ both columns    │ ─►│ExternalReference│
 │                 │   │ExternalReference null│   │ agree           │   │                 │
 └─────────────────┘   └──────────────────────┘   └─────────────────┘   └─────────────────┘
   V1 ✔                  V1 ✔  V2 ✔                 V1 ✔  V2 ✔            V3 ✔
                         V3 only for its own rows   ── V3 may start ──    V1 ✘  V2 ✘
```

Migrations: `S1_Initial`, `S2_ExpandExternalReference`, `S2B_BackfillExternalReference`,
`S3_ContractDropCustomerReference`. A normal installation applies them all in order; only the
experiment stops in between.

**S2B is a step, not a detail.** The expand copies the rows that exist when it runs; a V1 instance
that stays up keeps writing only the old column, and V3 reads only the new one. S2B is what closes
that window, and it is deliberately re-runnable so an operator can repeat it after stopping a
straggler. Switching V3 on between S2 and S2B is exactly the mistake this layout makes visible.

**"Both columns agree" is verified, not assumed.** S2B copies and then checks, and fails if any row
still holds an old and a new value that differ — so the box above is a state the gate enforces
rather than a state it hopes for. Rows V3 wrote (new column only, old one null) are correct and
pass. The comparison runs under a binary collation, so a case difference counts as a difference
instead of being absorbed by the database's default collation. S3 applies the same check again
before it drops anything.

A later migration, `S4_AddCompanyAndUserForeignKeys`, adds the ownership constraints on orders,
prices and idempotency records, and `S5_AddOrderLineNumber` stores each order line's position.
Both are ordinary schema changes, not further states of this experiment.

## Who is who

| Version | Where | Reads | Writes |
|---|---|---|---|
| V1 | `samples/Migration.V1` | `CustomerReference` | `CustomerReference` |
| V2 | `samples/Migration.V2` | `ExternalReference` **first**, falls back to `CustomerReference` | both |
| V3 | this API | `ExternalReference` | `ExternalReference` |

The helpers are separate executables started as separate processes, with the connection string
passed through `MIGRATION_DB_CONNECTION` and never printed. They speak a fixed JSON contract:
`version`, `operation`, `orderId`, `reference`, `source`.

## Tests

[`MigrationCompatibilityTests`](../../tests/B2B.Ordering.Tests/MigrationCompatibilityTests.cs)
gives every scenario its own disposable database, so the ordinary suite keeps running against the
final schema:

`V1WorksOnS1AndS2` · `V2ReadsV1BeforeBackfill` · `V1ReadsV2OnS2` ·
`LateV1WriteIsInvisibleToV3UntilTheBackfillStep` · `BackfillStepIsRepeatable` ·
`LateV1WriteIncludedInFinalBackfill` · `BackfillGateIsRefusedWhenTheColumnsDisagree` ·
`RepeatedBackfillIsRefusedWhenTheColumnsDisagree` ·
`BackfillGateTreatsACaseDifferenceAsADisagreement` ·
`BackfillGateAcceptsTheStatesThatAreNotDisagreements` ·
`ContractIsRefusedWhenTheColumnsStillDisagree` · `NullReferenceSurvives` · `EmptyReferenceSurvives` ·
`V3WorksOnS2` · `V3WorksOnS3` · `ContractRejectsV1AndV2AsExpected` ·
`RollbackToV2RequiresReverseBackfill`

`NullReferenceSurvives` and `EmptyReferenceSurvives` are the two ends of "missing": a null
reference and a genuinely empty one, each followed through S2, the backfill gate and S3. The empty
string is a persistence question only — V3's own API normalises a blank `externalReference` to
null, which `OrderTests.NormalizesBlankExternalReferenceToNull` asserts separately, and mixing the
two up is how a round-trip bug hides.

Every experiment database is seeded with the same synthetic companies and users as the rest of the
suite, because `Orders` really does have foreign keys onto them.

`V3WorksOnS2` and `V3WorksOnS3` use today's API binary as V3. That binary also reads the
`OrderLines.LineNumber` column added later by S5, so those two tests apply S5's own operations on
top of the experiment state first. S5 is additive and touches only `OrderLines`, which no step of
the experiment changes; the question the tests ask about the reference column is the same.

`ContractRejectsV1AndV2AsExpected` asserts the failure rather than leaving a red test behind: after
the drop, the old binaries *should* fail, and the test proves the failure names the missing column.

## Running the demo by hand

```
pwsh -File scripts/migration-demo.ps1
```

It creates a uniquely named disposable database, walks S1 → S2 → S2B → S3 with the helpers, prints
each JSON result, and drops the database at the end. The default development database is untouched.
