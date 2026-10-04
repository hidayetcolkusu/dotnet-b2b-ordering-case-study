# Initial verification — 2026-09-10

Historical record of the first complete run (96 tests, before any commit or CI run existed). The
current verified state is in [`../verification.md`](../verification.md).

**Date:** 2026-09-10 (last re-run the same day, after the fixes below)
**Commit:** not committed at the time of this run — the working tree was the finished state
described here.
**Machine:** Windows 11 Pro 10.0.26200, Docker Desktop 29.6.1 (Linux containers)
**SDK:** .NET 10.0.400 · runtime Microsoft.NETCore.App / Microsoft.AspNetCore.App 10.0.11
**SQL Server:** `mcr.microsoft.com/mssql/server:2022-latest` pinned by digest
`sha256:97b448857967be55e005424a660056fe6d51814435804dc07e8f79f028bab5fb`
(the image reference in code and Compose is `mcr.microsoft.com/mssql/server@sha256:97b4...b5fb`)

## Commands that were run and passed

```
dotnet --version                                        → 10.0.400
docker info                                             → Server 29.6.1, OSType linux
dotnet tool restore                                     → ok (dotnet-ef 10.0.11)
dotnet restore --locked-mode                            → ok
dotnet build --no-restore --configuration Release       → 0 warnings, 0 errors
dotnet format --verify-no-changes --no-restore          → no changes
dotnet test --no-build --configuration Release
        -- --report-trx --report-trx-filename release.trx
```

## Test result

96 tests, **96 passed**, 0 failed, 0 skipped, 1m 23s. From `TestResults/release.trx`:

| Suite | Passed |
|---|---|
| `OrderTests` | 19 |
| `ErrorContractTests` | 17 |
| `AuthenticationTests` | 16 |
| `MigrationCompatibilityTests` | 12 |
| `IdempotencyTests` | 11 |
| `IsolationTests` | 7 |
| `OpenApiTests` | 5 |
| `SchemaConstraintTests` | 5 |
| `ModuleBoundaryTests` | 3 |
| `HealthTests` | 1 |

Warnings are errors (`TreatWarningsAsErrors`), so a clean build means no warnings either.

## Clean setup, run by hand

```
cp .env.example .env      # then a locally generated password was written into it
docker compose up -d
powershell -File scripts/init-dev.ps1
dotnet run --project src/B2B.Ordering.Api/B2B.Ordering.Api.csproj
```

`init-dev.ps1` reported: container healthy → connection string written to user-secrets →
migrations `S1_Initial`, `S2_ExpandExternalReference`, `S2B_BackfillExternalReference`,
`S3_ContractDropCustomerReference`, `S4_AddCompanyAndUserForeignKeys` applied → seed data written.
No password or connection string was printed. For the final run the development database was
dropped first, so this was a genuine from-scratch setup.

The token was minted with:

```
dotnet user-jwts create --project src/B2B.Ordering.Api/B2B.Ordering.Api.csproj \
  --issuer b2b-ordering-dev --audience b2b-ordering-api \
  --claim app_user_id=a1111111-1111-4111-8111-111111111111
```

## HTTP smoke checks against the running API

| # | Request | Result |
|---|---|---|
| 1 | A Buyer `POST /api/orders`, `SKU-001` ×2 | `201`, `Location: /api/orders/<id>`, `totalAmount: 160.00`, `currency: TRY` |
| 2 | A Buyer `GET /api/orders/{id}` | `200`, same body values |
| 3 | A Viewer `GET /api/orders/{id}` | `200` |
| 4 | A Viewer `POST /api/orders` | `403`, `errorCode: role_not_allowed`, `application/problem+json` |
| 5 | Same `Idempotency-Key`, same payload | `201`, byte-identical body and same `Location` |
| 6 | Same key, quantity changed to 3 | `409`, `errorCode: idempotency_key_conflict` |
| 7 | A Buyer's token with `X-Company-Id` = company B | `403`, `errorCode: company_access_denied` |
| 8 | No `Authorization` header | `401`, `errorCode: unauthenticated` (asserted by `ErrorContractTests`; the smoke capture was truncated before this field) |
| 9 | Malformed JSON body | `400`, `errorCode: malformed_request` |
| 10 | `?page=2147483647&pageSize=100` | `200` with `{"items":[],"page":2147483647,"pageSize":100,"totalCount":1}` — no 500 |
| 11 | Validation error requested with `Accept: application/xml` | `400 application/problem+json`, body intact |
| 12 | `401` requested with `Accept: text/plain` | `401 application/problem+json`, body intact |

## OpenAPI and Swagger checks against the running API

| Request | Result |
|---|---|
| `GET /swagger` and `GET /swagger/index.html` | `200 text/html` |
| `GET /openapi/v1.json` | `200`, OpenAPI **3.1.1** |
| Document security | `[{"Bearer": []}]`, scheme `type: http`, `scheme: bearer`, `bearerFormat: JWT` |
| `/health` operation | `security: []` — the anonymous endpoint opts out of the document-wide requirement |
| `POST /api/orders` operation | no per-operation `security` override, so the document-wide Bearer applies |
| `POST /api/orders` declared responses | `201/400/401/403/404/409/415/503` |
| `POST /api/orders` parameters | headers `X-Company-Id`, `Idempotency-Key` |
| `GET /api/orders` parameters | query `page`, `pageSize`; header `X-Company-Id` |
| `GET /api/orders/{id}` parameters | path `id`; header `X-Company-Id` |
| Component schemas | `ApiProblemDetails`, `CreateOrderRequest`, `CreateOrderLineRequest`, `CreateOrderResponse`, `OrderLineResponse`, `OrderListResponse` |
| `ApiProblemDetails` properties | `type`, `title`, `status`, `detail`, `instance`, `errorCode`, `traceId`, `errors` |
| `409` response content type | `application/problem+json` |

`OpenApiTests` (5 tests) asserts the same facts in the test suite, so a broken document fails the
build rather than surprising a reader. Outside Development and Testing neither route is mapped.

Every failure body was `application/problem+json` and carried `status`, `title`, `instance` and
`traceId`.

Example success body (indented for readability):

```json
{
  "id": "01a08caa-4924-7fe5-8340-c6a0a641b53d",
  "companyId": "11111111-1111-4111-8111-111111111111",
  "totalAmount": 160.00,
  "currency": "TRY",
  "createdAtUtc": "2026-09-10T18:52:45.2209127Z",
  "externalReference": "PO-100",
  "lines": [{ "sku": "SKU-001", "quantity": 2, "unitPrice": 80.00, "lineTotal": 160.00 }]
}
```

Example error body:

```json
{
  "type": "https://tools.ietf.org/html/rfc9110#section-15.5.4",
  "title": "Role not allowed",
  "status": 403,
  "detail": "This operation requires the Buyer role in the requested company.",
  "instance": "/api/orders",
  "errorCode": "role_not_allowed",
  "traceId": "00-…"
}
```

## Defects reproduced and fixed

Four defects were found after the first complete run. Each was first reproduced with a failing test,
then fixed; the counts above are from the run after all four fixes.

| Finding | Reproduced as | Fix |
|---|---|---|
| Orders could be written for a company or user that does not exist — the ownership relationships had no foreign keys | `SchemaConstraintTests` (4 of 5 red: orders, prices and idempotency records all accepted unknown ids) | `S4_AddCompanyAndUserForeignKeys` adds `FK_Orders_Companies_CompanyId`, `FK_Orders_Users_CreatedByUserId`, `FK_CompanyProductPrices_Companies_CompanyId`, `FK_IdempotencyRecords_Companies_CompanyId`, `FK_IdempotencyRecords_Users_UserId`. The tenant write guard compares against the *caller's* company, so it can never see this case. |
| The final backfill was not a separate step, so a row written by V1 after the expand read as `null` for V3 before the contract | `LateV1WriteIsInvisibleToV3UntilTheBackfillStep` (red: the reference was null and stayed null) | New migration `S2B_BackfillExternalReference` sits between expand and contract as the gate before V3 is switched on. `BackfillStepIsRepeatable` and `ContractIsRefusedWhenTheColumnsStillDisagree` cover re-running it and the mismatch guard. |
| A very large `page` produced a 500 | `VeryLargePageNumberReturnsAnEmptyPageNotAnError` (red: `500` instead of `200`) | `(page - 1) * pageSize` overflowed `int` and reached SQL Server as a negative offset. The offset is now computed in `long`, and a page past the end returns an empty page with the real `totalCount`. |
| Some `Accept` headers lost the error body | `ErrorBodySurvivesAnAcceptHeaderThatDoesNotMentionJson`, `StatusOnlyFailuresAlsoKeepTheirBodyForAnyAccept` (red: content type `null` on the exception path, `text/plain` on the status-code path) | `IProblemDetailsService` declines to write when the request accepts neither JSON nor `*/*`. `ProblemResponseWriter` still gives it first refusal, then writes `application/problem+json` itself when it declines. Both doors go through it. |

None of these changed the architecture: the module boundary, the tenant model, the idempotency
transaction and the HTTP contract are untouched.

## Defects found and fixed during earlier runs

The first smoke check showed `createdAtUtc` ending in `Z` in the create response but not in the `GET`
response: SQL Server `datetime2` has no time zone, so the value read back had
`DateTimeKind.Unspecified`. A value converter now restores `DateTimeKind.Utc` on read, and
`CreateAndGetReturnTheSameCreatedAtUtc` in `OrderTests` locks the behaviour in.

The OpenAPI check showed `/health` inheriting the document-wide Bearer requirement even though it
is anonymous, so the schema was telling clients to authenticate for a liveness probe.
`AnonymousOperationTransformer` now clears it from any endpoint carrying `AllowAnonymous`, and
`AnonymousEndpointIsNotMarkedAsProtected` locks that in.

Both runs above are after the fixes.

## What was not run

- **Remote CI was not executed.** `.github/workflows/ci.yml` exists and mirrors the commands above,
  but no run on GitHub Actions has happened; the workflow file is not evidence of a green CI.
- No commit, tag or push was made, and nothing was published anywhere.
- `pwsh` (PowerShell 7) is not installed on this machine, so both scripts were run with Windows
  PowerShell 5.1 and were adjusted to work under both.

## Secret hygiene

`.env` is git-ignored and holds a locally generated password. The connection string lives in
dotnet user-secrets, outside the repository. No token, password or connection string appears in
this file, in the results files, or in script output. The token used above was minted locally and
is not recorded here.
