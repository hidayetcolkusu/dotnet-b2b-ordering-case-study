# ADR 0005 — Expand and contract for a renamed column

**Status:** Accepted

## Context

`Order` carried a nullable `CustomerReference`. The name no longer matched what the field means, so
it had to become `ExternalReference`. A single `RenameColumn` migration breaks every running
instance the moment it is applied.

Note what is **not** being changed: no money column is touched. The experiment moves one nullable
string column with unchanged meaning, and the HTTP field is called `externalReference` from the
very first version to the last.

## Decision

Three schema states and three application versions.

| Step | Schema | Applications that work |
|---|---|---|
| S1 | `CustomerReference` only | V1 |
| S2 expand | both columns, new one nullable | V1 and V2 together |
| S2B backfill | unchanged | V1 and V2 still fine; the gate before V3 |
| S3 contract | `ExternalReference` only | V3 |

- **V1** knows only the old column.
- **V2** writes the same value to *both* columns and, on read, prefers the new one and falls back
  to the old one for rows V1 wrote.
- **V3** — the API in this repository — uses only the new column.

EF's generated diff for S2 was a `RenameColumn`; it was replaced by an additive `AddColumn` plus a
re-runnable backfill.

**The final backfill is its own migration (`S2B`), not a side effect of another step.** This is the
part that is easy to get wrong. S2 copies the rows that exist *at the moment it runs*, but a V1
instance keeps writing only the old column for as long as it stays up. Those rows are invisible to
V3, which reads only the new column — so if V3 were switched on straight after S2, it would read
`null` for every reference V1 wrote in the meantime. S2B is the gate between "the last V1 and V2
writer has stopped" and "V3 may be switched on", and
`LateV1WriteIsInvisibleToV3UntilTheBackfillStep` demonstrates both sides of it.

The statement is re-runnable — it touches only rows that have not been copied yet — so an operator
can run it again after stopping a straggling instance.

**Copying is only half of the gate.** S2B also verifies that the two columns now agree and fails if
they do not (`BackfillGateIsRefusedWhenTheColumnsDisagree`, and the same verdict on the operator's
repeat path in `RepeatedBackfillIsRefusedWhenTheColumnsDisagree`). Without that, the gate would
report success on a database holding an old and a new value that differ, V3 would be switched on
over the new one, and the disagreement would only surface much later at the contract step. S3
repeats both the copy and the same check as a belt, and **refuses to drop the column** if they
still disagree (`ContractIsRefusedWhenTheColumnsStillDisagree`), leaving the old column in place so
the mismatch can be investigated.

Two details of that check are decisions, not accidents:

- **A row V3 wrote is not a mismatch.** Only rows whose `CustomerReference` is populated are
  examined; the new-column-only shape is correct once V3 is on, and failing it would make the gate
  unusable exactly when it is needed (`BackfillGateAcceptsTheStatesThatAreNotDisagreements`).
- **The comparison is forced to a binary collation.** A database's default collation is typically
  case- and accent-insensitive, so a plain `<>` would call `PO-Ref` and `PO-REF` equal and wave a
  real difference through. A reference is an opaque identifier; a difference in case is a
  difference. `BackfillGateTreatsACaseDifferenceAsADisagreement` pins that down on real SQL Server.

Orders are immutable — there is no update or delete endpoint — so no writer can change an old value
after the backfill has copied it. That is what makes the final backfill safe.

## Rolling back

Before the contract step, going from V3 back to an old binary is possible, and the reverse backfill
is what makes it safe — but it is worth being exact about who actually needs it, because the two
old versions are not in the same position.

V3 on S2 writes the new column only, leaving the old one null on those rows. **V1 reads null**: the
old column is the only one it knows. **V2 does not.** V2 reads the new column first and falls back
to the old one, so it already sees the value before any reverse backfill has run —
`RollbackToV2RequiresReverseBackfill` asserts both halves, V1's null and V2's value, before it
synchronises anything.

The reverse backfill stays a required step regardless. It exists for the old column's own sake: V1
compatibility, and the agreement gate in S2B and S3, which both read `CustomerReference` and would
otherwise find nothing to agree with. Dropping the step because "V2 can read it anyway" would be
the wrong lesson to take from that assertion.

After the contract step, rolling straight back to an old binary is **not** supported. `Down()`
recreates the column and copies values back from the new one; it is a schema repair, not data
recovery. A value that only ever existed in the dropped column is gone.

## What the experiment proves, and what it does not

It proves persistence compatibility on a real SQL Server: separate operating-system processes
running the old and new code against the same database, with the exact SQL error after the contract
step asserted as the expected controlled outcome.

It does **not** prove zero downtime under live traffic, and it is not a gateway or auth
compatibility test across versions. `samples/Migration.V1` and `samples/Migration.V2` are small
console helpers, not copies of the API.

## Türkçe öğrenme notu

**Akış:** S1 → (expand) S2 → V2 devreye → V1 durdurulur → **(S2B) son backfill** → V3 → (contract) S3.

**Neden:** Ekleme geriye dönük uyumludur, silme değildir. Bu yüzden silme en sona, tüm eski
yazıcılar durduktan sonraya bırakılır.

**Alternatif:** Tek `RenameColumn` migration'ı. Çalışan her instance anında kırılır.

**Hata senaryosu:** Son backfill'i ayrı bir adım yapmamak. Expand sırasında yapılan kopyalama
yalnız o anki satırları kapsar; V1 sonrasında yazmaya devam ettiği için V3 açıldığında o
referansları null okur. Diğer klasik hatalar: V2 hâlâ çalışırken eski kolonu silmek ve backfill'i
tekrar çalıştırılamayacak şekilde yazmak.

**Üçüncü hata senaryosu:** Backfill adımını yalnız kopyalama sanmak. Kapı, V3'ü açan adım olduğu
için iki kolonun uyuştuğunu da doğrulamalı; yoksa farklı iki değer sessizce geçer ve sorun çok
sonra, contract adımında ortaya çıkar. Karşılaştırmayı veritabanının varsayılan collation'ına
bırakmak da aynı sınıfa girer: `PO-Ref` ile `PO-REF` eşit sayılır ve gerçek bir metin farkı
kaybolur.

**Dördüncü hata senaryosu:** "V3 yalnız yeni kolona yazınca V1 **ve V2** null okur" diye
öğretmek. V2 önce yeni kolonu okur; null okuyan yalnız V1'dir. Reverse backfill yine de
gereklidir — ama gerekçesi V2 değil, eski kolonun kendisi: V1 uyumluluğu ve S2B/S3 uyum kapısı.

**Kod yolu:** [`src/B2B.Ordering.Api/Migrations/`](../../src/B2B.Ordering.Api/Migrations/),
[`samples/`](../../samples/)

**Deney komutu:** `dotnet test --filter-class "*MigrationCompatibilityTests"` ve
`pwsh -File scripts/migration-demo.ps1`
