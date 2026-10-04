# Idempotency results

**SDK** 10.0.400 · **SQL Server 2022** container pinned by digest `sha256:97b4…b5fb`. Results
are from the current verified run (see [`verification.md`](verification.md) for the full
environment).

**Command**

```
dotnet test --no-build --configuration Release --filter-class "*IdempotencyTests"
```

Part of the full suite run: **11 tests, 11 passed, 0 failed.**

## What each test observed

| Test | Scenario | Observed |
|---|---|---|
| `SamePayloadReplaysTheStoredResponse` | Same user, company, key and payload twice | Both `201`, byte-identical body, same `Location`, **1** order in the database |
| `DifferentWhitespaceAndPropertyOrderStillReplays` | Same meaning, reformatted JSON with reordered properties | Replay, identical body, **1** order |
| `DifferentQuantityIsConflict` | Same key, quantity 2 → 3 | `409` `idempotency_key_conflict`; a third request with the original payload still returns the first body; **1** order |
| `ReorderedLinesAreADifferentPayload` | Lines swapped | `409` — line order is part of the meaning |
| `SameKeyForAnotherUserOrCompanyIsIndependent` | Same key string, three different (company, user) pairs | Three `201`, three distinct order ids |
| `TenConcurrentIdenticalRequestsProduceOneOrder` | 10 requests released together by a `Barrier` | All **10** returned `201` with one distinct body; **1** order; the single idempotency row had `OrderId`, `StatusCode = 201` and a non-empty `ResponseBody` |
| `ConcurrentDifferentPayloadsHaveOneWinner` | 2 concurrent requests, same key, different payloads | Exactly one `201` and one `409`; **1** order |
| `FailureBeforeCommitLeavesNothingBehind` | Injected fault just before `COMMIT` | `500`; **0** orders and **0** idempotency rows; the retry then succeeded with `201` and produced exactly 1 order |
| `LostResponseAfterCommitIsRecoveredByReplay` | Injected fault just after `COMMIT` | `500` to the caller, but **1** order existed; the next request with the same key returned `201` with the stored response and still **1** order |
| `RevokedMembershipCannotReplayTheStoredResponse` | Membership deactivated after the order was created | `403`; the response body contained no `totalAmount`, so the stored response was not exposed |
| `LockWaitTimeoutIsServiceUnavailableWithRetryAfter` | Reservation row held by an uncommitted transaction opened outside the API; second host configured with a 500 ms lock budget | `503`, `application/problem+json`, `errorCode: idempotency_busy`, `Retry-After: 1` |

Counts were scoped to the test's own company, user, key and order ids, not to whole tables.

## Honest reading of the concurrency result

The test asserts all ten return `201` with one body, **one order and one completed record**. The
single order is guaranteed by the unique index. The ten `201`s follow from the losers waiting on
the winner's key lock and then replaying; they hold only while the winner commits inside the
5-second lock budget. Past that budget a waiting attempt gets `503` (see
`LockWaitTimeoutIsServiceUnavailableWithRetryAfter`).

*Corrected 2026-10-04: this paragraph previously said the test asserted only the single order.*

## Faults are test-only

The failure points come from `ICreateOrderFaultHook`. The production registration
(`NoFaultHook`) does nothing, and the controllable implementation lives in the test project. There
is no endpoint, header or configuration setting that can trigger a fault in a running API.

## Limitations

Idempotency records are never deleted and have no TTL, so the table grows without bound. EF's
retry execution strategy is deliberately disabled: retrying a command inside a manual transaction
is not the same as retrying the transaction, and adding it would need its own decision.
