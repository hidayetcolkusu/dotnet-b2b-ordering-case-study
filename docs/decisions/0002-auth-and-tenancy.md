# ADR 0002 — Verified identity and the tenant trust boundary

**Status:** Accepted

## Context

A B2B API has two separate questions to answer on every request: *who is calling* and *may they act
for this company*. Conflating them is how tenant leaks happen.

## Decision

**Identity comes only from a validated token.** JwtBearer validates signature, issuer, audience and
lifetime with zero clock skew. After validation the `app_user_id` claim must parse as a non-empty
GUID; if it does not, authentication fails (401), because there is no identity to act on.

**`X-User-Id` is ignored entirely.** It is not read anywhere in the codebase. A test sends company
B's user id alongside company A's token and asserts the result is still 403.

**`X-Company-Id` selects a target, it does not grant anything.** The Access resolver checks an
active user, an active company and an active membership in the database, and reads the role from
there. A `role` claim inside the token is never consulted — a Viewer whose token claims Buyer is
still refused.

**Status codes are separated on purpose.** No or invalid token → 401. Valid identity, no membership
or wrong role → 403. Valid identity, missing or malformed company header → 400. Another company's
order → 404, not 403, so existence is not leaked.

**`CallerContext` is scoped and write-once.** It is initialised once by the company context filter
and cannot change within a request. Reading it before initialisation throws. That is what makes the
tenant query filters fail closed instead of widening to every company.

**Three independent write guards.** Query filters cover reads. `SaveChanges` refuses any tenant
entity whose `CompanyId` differs from the caller's. Independently, `OrderLine` has a composite
foreign key onto `Orders (CompanyId, Id)`, so the database refuses a line attached to another
company's order even if application code is bypassed.

The third is ordinary referential integrity, and it covers a case the other two structurally
cannot: the write guard compares an entity's company against **the caller's**, so it can tell that
a company is *wrong*, but never that it does not *exist*. `Orders` therefore has foreign keys onto
`Companies` and `Users`, as do `CompanyProductPrices` and `IdempotencyRecords`. Without them an
order owned by a company that was never created would be perfectly acceptable to every application
check. `SchemaConstraintTests` writes through the maintenance path — which bypasses the application
guards on purpose — so what is left is exactly what the database refuses.

**Maintenance is not reachable over HTTP.** Seeding and migrations run inside a `MaintenanceScope`
that can only be opened by code outside the request pipeline. No header, query string or missing
context turns into seed authority.

## The one supported authentication mode

This repository implements exactly one mode, named in code as
`AuthenticationSetup.DemoMode` = `LocalSymmetricDemo`: demo tokens signed with a symmetric key that
lives in local configuration — user-secrets, `dotnet user-jwts`, or a random per-fixture key in the
tests. It is a development convenience, not an identity provider integration, and integrating a
real one is out of scope for this case study.

Because that is the only mode on offer, **startup is refused outright in any environment other than
Development and Testing.** The refusal does not depend on how the key was configured, and it fires
even when no key is configured at all:

- `Jwt:SigningKey` — the application's own path.
- `Authentication:Schemes:Bearer:SigningKeys` — the path `dotnet user-jwts` writes and the
  framework binds by itself. An earlier version of the guard only inspected `Jwt:SigningKey`, so a
  key arriving through this path started the API without complaint.
- Nothing at all — the built-in demo issuer `b2b-ordering-dev` and audience `b2b-ordering-api` are
  themselves the unsupported mode. An API that starts in Production but can never authenticate
  anyone is a worse outcome than one that refuses to start.

The refusal message names which configuration paths carried key material and never their values.

What this is not a claim about: it says nothing about whether an unverified token would have been
accepted or whether tenant data was reachable. It is a startup contract, checked before any
connection is opened.

**Enforcement:**
[`AuthenticationStartupTests`](../../tests/B2B.Ordering.Tests/AuthenticationStartupTests.cs) covers
Production and Staging across all three key sources at registration level, and builds the real
`Program` host to assert that it refuses; it also asserts that Development still starts and serves
`/health`. None of it needs a database.

## Türkçe öğrenme notu

**Akış:** Token doğrulanır → `app_user_id` alınır → header'daki şirket için üyelik DB'den okunur →
`CallerContext` bir kez dolar → tenant filtreleri ve yazma koruması bu değere bakar.

**Neden:** İstemciden gelen hiçbir kimlik/şirket/rol bilgisi güvenilir değildir; hepsi sunucuda
yeniden doğrulanır.

**Alternatif:** Rolü token claim'inden okumak. Tek round-trip kazandırır ama üyelik iptal
edildiğinde token süresi bitene kadar yetki devam eder.

**Hata senaryosu:** Şirket header kontrolünü authentication'dan önce yapmak. O zaman token'sız
istek 401 yerine 400 alır ve gerçek hata gizlenir.

**İkinci hata senaryosu:** Ortam kontrolünü tek bir configuration yoluna (`Jwt:SigningKey`)
bağlamak. Anahtar `dotnet user-jwts`'in yazdığı `Authentication:Schemes:Bearer:SigningKeys`
yolundan geldiğinde ya da hiç anahtar verilmediğinde guard sessizce devre dışı kalır; uygulama
Production'da başlar. Doğrusu: desteklenen kipi adıyla tanımlayıp ortam dışı başlangıcı
koşulsuz reddetmek.

**Kod yolu:**
[`AuthenticationSetup.cs`](../../src/B2B.Ordering.Api/Shared/Auth/AuthenticationSetup.cs),
[`CompanyContextFilter.cs`](../../src/B2B.Ordering.Api/Shared/Tenancy/CompanyContextFilter.cs)

**Deney komutu:** `dotnet test --filter-class "*AuthenticationTests"` ve
`dotnet test --filter-class "*AuthenticationStartupTests"`
