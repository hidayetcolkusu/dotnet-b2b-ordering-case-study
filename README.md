# dotnet-b2b-ordering-case-study

[![ci](https://github.com/hidayetcolkusu/dotnet-b2b-ordering-case-study/actions/workflows/ci.yml/badge.svg?branch=main)](https://github.com/hidayetcolkusu/dotnet-b2b-ordering-case-study/actions/workflows/ci.yml)

> **Türkçe özet.** Kurumsal B2B sipariş alma senaryosunu .NET 10 ve SQL Server ile modelleyen,
> sentetik bir örnek çalışma. Odak noktası: şirkete özel fiyatlandırma, kullanıcının hangi şirket
> adına işlem yapabildiğinin gerçekten doğrulanması, tutarlı hata sözleşmesi, tekrar eden
> isteklerin tek sipariş üretmesi ve veritabanı şeması değişirken eski sürümlerin çalışmaya devam
> etmesi. Bütün testler gerçek SQL Server container'ı üzerinde koşar. Öğrenme rehberi:
> [`docs/learning/code-walkthrough.md`](docs/learning/code-walkthrough.md).

---

## The problem

In B2B ordering, "the user is logged in" is not the interesting question. The interesting questions
are:

- May this user place an order **for this company**, and in which role?
- Which price applies — the catalogue list price, or a price negotiated with this company?
- When the buyer double-clicks, or a client retries after a timeout, does the supplier get one
  order or two?
- When an integration receives a `400`, can it tell *why* without parsing prose?
- When the schema changes, does the deployment that is already running keep working?

This repository answers each of them with running code and a test that would fail if the answer
were wrong.

## Scope and honesty

This is an **independent, synthetic case study**. It contains no real source code, customer data or
internal screens from any employer, and no lab result here is presented as an outcome achieved in a
past job. It is not a copy of a production system, and it is not "production ready": local JWT
minting is a development convenience, idempotency records grow without bound, both modules share
one database, and the migration experiment proves *persistence* compatibility, not zero downtime
under live traffic.

## Architecture at a glance

```
HTTP ─► exception handler ─► status code pages ─► routing ─► authentication ─► authorization
     ─► company context (order endpoints only) ─► handler ─► EF Core ─► SQL Server 2022
```

Two modules in one deployable: **Access** (company, user, membership) and **Ordering** (product,
price, order, idempotency). Ordering reaches Access only through one contract interface, and a test
that reads IL proves it.

- [Architecture overview](docs/architecture/overview.md)
- [Request flow](docs/architecture/request-flow.md)
- [Migration compatibility experiment](docs/architecture/migration-compatibility.md)
- Decisions: [0001 modules](docs/decisions/0001-module-boundaries.md) ·
  [0002 auth and tenancy](docs/decisions/0002-auth-and-tenancy.md) ·
  [0003 idempotency](docs/decisions/0003-idempotency.md) ·
  [0004 error contract](docs/decisions/0004-error-contract.md) ·
  [0005 migrations](docs/decisions/0005-migration-compatibility.md)

## Requirements

- .NET SDK **10.0.400** (pinned in `global.json` with `rollForward: disable`)
- Docker able to run Linux containers — used by Testcontainers for tests, and by Compose for the
  development database

## Setup

```
dotnet tool restore
dotnet restore --locked-mode
```

Create a local database password before the first `docker compose up`:

```
cp .env.example .env
# then edit .env and replace the placeholder with a password you generate locally.
# SQL Server needs at least 8 characters with upper case, lower case, digits and symbols.
```

`.env` is git-ignored. Then:

```
docker compose up -d
pwsh -File scripts/init-dev.ps1          # Windows PowerShell 5.1 also works:
                                         # powershell -File scripts/init-dev.ps1
dotnet run --project src/B2B.Ordering.Api/B2B.Ordering.Api.csproj
```

`init-dev.ps1` waits for the container to report healthy, stores the connection string in
**dotnet user-secrets** (never in a file inside the repository), and runs the API's
`--initialize-db` mode to apply migrations and seed synthetic data. It prints no secret. A normal
application start never applies migrations.

The development database listens on `127.0.0.1:14330` only.

**On Windows, clone into a short path.** `Microsoft.Data.SqlClient` loads its native SNI library
from `bin/.../runtimes/win-*/native/`, and in a deep checkout that path crosses the 260-character
`MAX_PATH` limit. The migration helper executables then fail with
`The type initializer for 'Microsoft.Data.SqlClient.TdsParser' threw an exception.` — a message
that says nothing about paths — and every `MigrationCompatibilityTests` case goes red while the
rest of the suite passes. Something like `C:\src\` is enough; Linux and macOS have no such limit.
It reproduces by running the suite from a deep temporary directory.

Need a second, disposable instance beside it — for a clean-setup experiment, say? Override
`MSSQL_CONTAINER_NAME` and `MSSQL_HOST_PORT` for compose, and pass `-ContainerName`, `-Port` and
`-Database` to `init-dev.ps1` (`scripts/migration-demo.ps1` takes the first two as well). All of
them default to the development values, so the commands above are unchanged.

## Swagger

With the API running in Development, the interactive docs are at
**<http://localhost:5080/swagger>** and the raw schema at
**<http://localhost:5080/openapi/v1.json>**.

The document is generated by `Microsoft.AspNetCore.OpenApi` from the endpoints themselves;
Swashbuckle supplies only the UI, so there is no second copy of the schema to drift. It describes
what the API really does: the `X-Company-Id` and `Idempotency-Key` headers (which an endpoint
filter reads, so they are added by a transformer), every failure status with the real
`errorCode`/`traceId` body shape, and a Bearer scheme that the **Authorize** button feeds into the
actual JwtBearer pipeline — paste a `user-jwts` token there and *Try it out* works end to end.

Both routes exist only in **Development** and **Testing**. Publishing the schema of a private API
is a decision, not a default, so nothing is exposed in any other environment. `/health` opts out of
the document-wide Bearer requirement, because it really is anonymous.

## Getting a token

Local tokens come from the Microsoft `user-jwts` tool. Signature validation is **not** disabled for
convenience, and this local demo mode is the only authentication mode the API implements — starting
it outside Development or Testing is refused rather than downgraded. In a second terminal:

```
dotnet user-jwts create --project src/B2B.Ordering.Api/B2B.Ordering.Api.csproj \
  --issuer b2b-ordering-dev \
  --audience b2b-ordering-api \
  --claim app_user_id=a1111111-1111-4111-8111-111111111111
```

That GUID is the seeded **A Buyer** from
[`SeedIds`](src/B2B.Ordering.Api/Shared/Persistence/SeedIds.cs). The other seeded identities:

| Who | Id | Company |
|---|---|---|
| A Buyer | `a1111111-1111-4111-8111-111111111111` | A (`11111111-1111-4111-8111-111111111111`) |
| A Viewer | `a2222222-2222-4222-8222-222222222222` | A |
| B Buyer | `b1111111-1111-4111-8111-111111111111` | B (`22222222-2222-4222-8222-222222222222`) |
| Shared Buyer | `c1111111-1111-4111-8111-111111111111` | A and B |

## Example request

The token is read from an environment variable so it never ends up in shell history. On Windows use
`curl.exe`, because `curl` is an alias for `Invoke-WebRequest` in PowerShell.

```
$env:TOKEN = "<paste the token printed by user-jwts>"

curl.exe -i -X POST http://localhost:5080/api/orders `
  -H "Authorization: Bearer $env:TOKEN" `
  -H "X-Company-Id: 11111111-1111-4111-8111-111111111111" `
  -H "Idempotency-Key: demo-001" `
  -H "Content-Type: application/json" `
  -d '{\"lines\":[{\"sku\":\"SKU-001\",\"quantity\":2}],\"externalReference\":\"PO-100\"}'
```

`SKU-001` lists at 100 TRY, but company A negotiated 80 TRY, so the response total is **160 TRY**.
Send the same request again with the same `Idempotency-Key` and you get the identical body and
`Location` back — still one order. Change the quantity and keep the key, and you get `409`.

## HTTP contract

| Operation | Requires | Result |
|---|---|---|
| `POST /api/orders` | JWT, `X-Company-Id`, `Idempotency-Key`, Buyer | `201` + `Location` + order summary |
| `GET /api/orders/{id}` | JWT, `X-Company-Id`, Buyer or Viewer | `200`, or `404` if it belongs to another company |
| `GET /api/orders?page=1&pageSize=20` | JWT, `X-Company-Id`, Buyer or Viewer | `200` with `items`, `page`, `pageSize`, `totalCount` |
| `GET /health` | nothing | `200`, no configuration or connection detail |
| `GET /openapi/v1.json`, `GET /swagger` | nothing | the schema and Swagger UI, Development and Testing only |

Unknown JSON members are rejected. Every failure of a supported request is
`application/problem+json` with an `errorCode` and a `traceId`.

## Tests

```
dotnet test
```

Docker must be running; Compose does **not** need to be up. Testcontainers owns the test database
locally and in CI, so there is only one way a test database is ever created. There is no SQLite or
in-memory substitute: foreign keys, transactions, unique races and migrations all have to be real.

Named suites (this project uses xUnit v3 on Microsoft.Testing.Platform, so filtering is
`--filter-class`, not the older `--filter FullyQualifiedName~`):

```
dotnet test --filter-class "*AuthenticationTests"
dotnet test --filter-class "*AuthenticationStartupTests"
dotnet test --filter-class "*IsolationTests"
dotnet test --filter-class "*OrderTests"
dotnet test --filter-class "*ErrorContractTests"
dotnet test --filter-class "*IdempotencyTests"
dotnet test --filter-class "*ModuleBoundaryTests"
dotnet test --filter-class "*MigrationCompatibilityTests"
dotnet test --filter-class "*OpenApiTests"
dotnet test --filter-class "*SchemaConstraintTests"
dotnet test --filter-class "*LineNumberMigrationTests"
```

The critical ones, and what they would catch:

| Test | Would fail if |
|---|---|
| `SpoofedUserHeaderCannotChangeIdentity` | a header could override the token identity |
| `TheRealHostRefusesToStart` | the local demo auth mode could start the API outside Development/Testing |
| `BackfillGateIsRefusedWhenTheColumnsDisagree` | the gate that opens V3 passed over two disagreeing values |
| `TokenRoleClaimDoesNotOverrideDatabaseRole` | a role claim could out-vote the membership row |
| `CompanyACannotReadCompanyBOrder` | tenant isolation leaked across companies |
| `CrossTenantLineForeignKeyRejected` | the database allowed a line on another company's order |
| `OrderForUnknownCompanyIsRejected` | an order could be stored for a company that does not exist |
| `VeryLargePageNumberReturnsAnEmptyPageNotAnError` | a huge `page` overflowed the offset into a 500 |
| `ErrorBodySurvivesAnAcceptHeaderThatDoesNotMentionJson` | an unusual `Accept` header silently dropped the error body |
| `LateV1WriteIsInvisibleToV3UntilTheBackfillStep` | V3 was switched on before the final backfill |
| `PriceSnapshotDoesNotChange` | a later price change rewrote history |
| `GetReturnsTheLinesInTheOrderTheyWereSubmitted` | a read reshuffled the lines, so a GET disagreed with the stored replay |
| `ExistingMultiLineOrdersAreNumberedByTheBackfill` | upgrading a database with existing multi-line orders to S5 failed or left duplicate positions |
| `TenConcurrentIdenticalRequestsProduceOneOrder` | a double-click produced two orders |
| `FailureBeforeCommitLeavesNothingBehind` | a failed attempt left a half-written reservation |
| `ContractRejectsV1AndV2AsExpected` | the contract migration failed in an unexpected way |
| `AnonymousEndpointIsNotMarkedAsProtected` | the schema told clients to authenticate for a liveness probe |

## Migration demo

```
pwsh -File scripts/migration-demo.ps1
```

Runs the S1 → S2 → **S2B backfill** → S3 walkthrough on a uniquely named disposable database and
drops it afterwards. It shows the window the separate backfill step closes: a row written by V1
after the expand reads as `null` for V3 until S2B runs. The default development database is not
touched.

## What actually ran

[`results/verification.md`](results/verification.md) is the current verified state: the commit,
SDK, SQL Server image digest, the exact commands, and the result.

- **116/116 tests pass** on real SQL Server 2022 (Testcontainers, pinned by digest), locally and on
  GitHub Actions (`ubuntu-latest`, cold NuGet cache, `--locked-mode` restore, format check).
- Coverage includes token-only identity and database-backed roles, tenant isolation in the API and
  in foreign keys, ten concurrent identical creates producing one order, failure-before-commit
  rollback, the S1 → S2 → S2B → S3 experiment with real V1/V2/V3 processes, and the S5 `LineNumber`
  backfill.
- `GetReturnsTheLinesInTheOrderTheyWereSubmitted` locks in that order lines are read back by their
  persisted `LineNumber`, not by Guid order, which SQL Server does not sort chronologically.

Focused records: [idempotency](results/idempotency.md) · [migration](results/migration.md) ·
[earlier dated runs](results/history/). A green build is a statement about the build, not a claim
that this is production ready; the limitations below still apply.

## Known limitations

- Idempotency records are never cleaned up and have no TTL; unbounded growth is a real constraint.
- One database for both modules; the shared `AppDbContext` is a documented exception to the module
  boundary, not an accident.
- Local JWT minting only; there is no identity provider integration and no public deployment. It
  is the *only* authentication mode implemented, so the API deliberately refuses to start in any
  environment other than Development and Testing, whichever configuration path supplies the key.
- The migration experiment covers persistence compatibility. It does not demonstrate zero downtime,
  and after the contract step a direct rollback to an old binary is not supported.
- Swagger and the OpenAPI document are Development/Testing only; there is no versioned, published
  schema artefact and no client generation step.
- No payments, stock reservation, ERP integration, admin UI, or tenant provisioning.
