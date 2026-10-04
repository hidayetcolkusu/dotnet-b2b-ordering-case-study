# ADR 0003 — Transaction based idempotency

**Status:** Accepted

## Context

A buyer double-clicks. A client retries after a timeout it cannot distinguish from a failure. Both
must produce exactly one order.

## Decision

**Scope:** order creation only. The unique key is `(CompanyId, UserId, Key)`, so two users may use
the same key string independently.

**Key rules:** 1 to 128 printable ASCII characters, compared case sensitively — the column is
`varchar(128)` with `Latin1_General_BIN2` collation, so `abc` and `ABC` are two different keys.

**Hash:** SHA-256 over the *normalised, validated* DTO, not over the raw bytes. SKUs are trimmed
and upper-cased, an empty reference becomes null, line order is preserved. So reformatting the JSON
or reordering its properties replays; reordering the **lines** is a different payload and conflicts.
Unknown JSON members are rejected, which is what stops a field from becoming a hidden business
input that never reaches the hash.

**Coordination in the handler, not an endpoint filter.** A filter cannot join the transaction, and
the whole guarantee depends on the reservation row and the order committing together:

```
begin transaction
  set lock timeout
  insert reservation row            ← the unique index decides the winner here
  load products, validate rules
  insert order + lines
  serialise the success response once
  complete the reservation with OrderId / status / body / Location
commit
```

An incomplete reservation is never committed, so a committed row always carries a replayable
response.

**Losing the race.** Only a unique violation on `UX_IdempotencyRecords_Company_User_Key` is treated
as a duplicate; every other database error is rethrown. The failed context is abandoned — never
re-used for another `SaveChanges` — and a brand new `AppDbContext` reads the committed record. Same
hash replays the stored bytes verbatim; a different hash is 409.

**Authorisation before replay.** Membership and role are checked on every attempt. A user whose
membership was revoked gets 403 and never sees the stored response.

**Bounded waiting.** `SET LOCK_TIMEOUT` limits how long an attempt waits on the reservation lock.
Exceeding it is 503 with `Retry-After: 1`, which uses the same ProblemDetails contract as every
other error.

**No retry strategy.** EF's execution strategy is deliberately off. Retrying a command inside a
manual transaction is not the same as retrying the transaction, and adding it later needs its own
decision.

## What is *not* promised

The database guarantees one order and one completed record per key: that is the unique index, not
timing. Every attempt that loses the insert waits on the winner's key lock, then either receives
the duplicate-key error once the winner commits (and replays) or inserts cleanly if the winner rolled
back. `TenConcurrentIdenticalRequestsProduceOneOrder` therefore asserts all ten return `201`. The
one timing-dependent outcome is the lock budget: a winner that holds its transaction longer than
`LOCK_TIMEOUT` turns waiting attempts into `503`, which the client is told to retry.

## Limits

Records are kept forever: there is no TTL and no cleanup job. In a real system that table grows
without bound and would need a retention policy.

## Türkçe öğrenme notu

**Akış:** Rezervasyon INSERT → iş kuralları → sipariş → yanıtı bir kez serialize et → kaydı
tamamla → commit.

**Neden:** Unique index'i "kim kazandı" kararı için kullanmak, ayrı bir kilit mekanizmasına gerek
bırakmıyor; transaction da atomikliği veriyor.

**Alternatif:** Önce kaydı commit edip sonra sipariş oluşturmak. O zaman commit ile sipariş
arasında düşen istek, yanıtı olmayan bir kayıt bırakır.

**Hata senaryosu:** Bütün `DbUpdateException`'ları duplicate saymak. FK ihlali veya bağlantı hatası
da "zaten yapıldı" diye yutulur.

**Kod yolu:**
[`CreateOrderHandler.cs`](../../src/B2B.Ordering.Api/Modules/Ordering/Create/CreateOrderHandler.cs),
[`IdempotencyStore.cs`](../../src/B2B.Ordering.Api/Modules/Ordering/Idempotency/IdempotencyStore.cs)

**Deney komutu:** `dotnet test --filter-class "*IdempotencyTests"`
