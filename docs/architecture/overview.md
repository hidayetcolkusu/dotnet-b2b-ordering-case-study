# Architecture overview

One ASP.NET Core application, one SQL Server database, two modules.

```
                      ┌─────────────────────────────────────────────┐
  HTTP request ─────► │ B2B.Ordering.Api                            │
                      │                                             │
                      │  Shared/                                    │
                      │    Auth          JwtBearer validation       │
                      │    Tenancy       CallerContext + filter     │
                      │    Errors        ProblemDetails contract    │
                      │    Persistence   AppDbContext, guards       │
                      │                                             │
                      │  Modules/Access          Modules/Ordering   │
                      │    Company               Product            │
                      │    User                  CompanyProductPrice│
                      │    CompanyMembership     Order / OrderLine  │
                      │    CompanyAccessResolver IdempotencyRecord  │
                      │         ▲                     │             │
                      │         └── ICompanyAccessResolver ◄────────┤
                      │             (the only seam)                 │
                      └─────────────────────────────────────────────┘
                                        │
                                        ▼
                                   SQL Server 2022
```

## Modules

**Access** decides whether a user may act for a company, and in which role. It owns `Company`,
`User` and `CompanyMembership`. Its tables carry no tenant query filter, because the membership
lookup is what establishes the tenant context in the first place.

**Ordering** owns pricing, orders and repeat-request handling: `Product`,
`CompanyProductPrice`, `Order`, `OrderLine`, `IdempotencyRecord`.

Ordering reaches Access only through `Modules/Access/Contracts`. That is not a claim, it is
checked: [`ModuleBoundaryTests`](../../tests/B2B.Ordering.Tests/ModuleBoundaryTests.cs) reads IL,
so a forbidden type used inside a method body fails the build too. The rule was verified by
introducing a violation on purpose and watching the test go red before it was removed.

## The documented shared exception

`Shared/Persistence/AppDbContext` merges both modules' entity mappings, and `SeedData` writes rows
for both. Both are exempted in the boundary test **by type name**. Alongside them one namespace is
exempted, `Shared/Persistence/Configurations`: the `IEntityTypeConfiguration<T>` classes that
declare each module's mapping. That is the full list — see
[ADR 0001](../decisions/0001-module-boundaries.md#the-infrastructure-exception-in-full) — and it is
kept narrow on purpose, because exempting the whole `Shared` namespace would make the rule
meaningless.

`Shared/Tenancy/CompanyContextFilter` needs no exemption at all: it consumes the Access *contract*,
which Ordering may use as well, and produces the tenant context Ordering reads, so neither module
needs to know about the other.

## Schema documentation

`Shared/OpenApi` builds the OpenAPI document from the endpoints with
`Microsoft.AspNetCore.OpenApi`, and Swashbuckle renders it at `/swagger`. Three transformers fill
the gaps the generator cannot see by itself: the document info, the Bearer scheme applied
document-wide (cleared again on anonymous endpoints), and the two tenant headers that an endpoint
filter reads rather than binding as parameters. Both routes are mapped only in Development and
Testing. `OpenApiTests` asserts the document really contains all of that, so a broken schema fails
the test run instead of surprising a reader.

## What is not here

No payments, no stock reservation, no ERP integration, no message broker, no outbox, no Redis, no
Kafka, no Kubernetes, no microservices, no generic repository, no MediatR, no AutoMapper. Each
would add surface without adding evidence for the questions this repository is about.

## Türkçe öğrenme notu

**Akış:** İstek `Program.cs` içindeki pipeline'a girer, kimlik doğrulanır, üyelik çözülür,
`CallerContext` dolar, sipariş handler'ı çalışır.

**Neden:** Tek deploy ve tek veritabanı, bu boyuttaki bir problem için en az operasyonel maliyetli
seçenek. Modül sınırı ise ileride ayrıştırma gerekirse kesim yerini bugünden belli ediyor.

**Alternatif:** Mikroservis ayrıştırması. Bağımsız ekip/deploy ihtiyacı ya da belirgin biçimde
farklı yük profili doğmadan bu maliyeti ödemek gerekmiyor (bkz. ADR 0001).

**Hata senaryosu:** `AppDbContext`'i istisna listesine almadan modül testini yazarsanız test
kırmızı olur; bütün Shared'i istisna yapmak ise sınırı anlamsızlaştırır.

**Kod yolu:** [`src/B2B.Ordering.Api/Program.cs`](../../src/B2B.Ordering.Api/Program.cs)

**Deney komutu:** `dotnet test --filter-class "*ModuleBoundaryTests"`
