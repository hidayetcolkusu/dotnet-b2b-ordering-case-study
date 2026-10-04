# ADR 0001 — One deployable, two modules

**Status:** Accepted

## Context

The domain has two clearly separate responsibilities: deciding whether a user may act for a
company, and applying company specific ordering rules. The question is where to draw a boundary
and how hard to make it.

## Options considered

| Option | What it buys | What it costs |
|---|---|---|
| One API, feature folders only | Simplest to build and read | No boundary at all; ordering code drifts into membership tables over time |
| One API, modules with a checked boundary | The seam is explicit and testable; one deploy, one database, one transaction | Discipline has to be enforced by a test, and the shared DbContext stays a documented exception |
| Separate services | Independent deployment and scaling | Distributed transactions, network failure modes, duplicated auth, far more operational work than this problem needs |

## Decision

One deployable with two modules. Ordering may use `Modules/Access/Contracts` and nothing else from
Access; Access may not reference Ordering at all.

### The infrastructure exception, in full

Three things in `Shared` are exempt from the rule, and the test
(`SharedInfrastructureIsTheOnlyPlaceThatSeesBothModules`) lists all three:

| Exemption | Why |
|---|---|
| `AppDbContext`, by type name | It merges both modules' entity mappings into one context. |
| `SeedData`, by type name | It writes synthetic rows for both modules. |
| `Shared.Persistence.Configurations`, by namespace | The `IEntityTypeConfiguration<T>` classes that map each module's entities. They are mapping code, not behaviour, and they live beside the context they configure. |

The third is the one exemption granted to a whole namespace rather than a named type, so it is
worth stating plainly rather than leaving it to be discovered in the test: it covers EF mapping
declarations and nothing else. The exception overall is deliberately narrow — exempting the whole
`Shared` namespace would make the rule meaningless.

`Shared/Tenancy/CompanyContextFilter` is **not** on the list and does not need to be: it consumes
the Access *contract*, which Ordering is allowed to use too.

## Enforcement

[`ModuleBoundaryTests`](../../tests/B2B.Ordering.Tests/ModuleBoundaryTests.cs) uses NetArchTest
(pinned at 1.3.2), which reads IL and therefore catches a forbidden type used only inside a method
body. The forbidden list is computed from the assembly at runtime rather than hard-coded, so adding
a new Access namespace cannot silently escape it.

The check was validated by adding a violation inside a method body of `ListOrdersHandler`,
observing the test fail, and then removing it — a rule nobody has seen fail is not a rule.

## Revisiting this

Split the modules into separate services when there is a concrete reason: independent teams that
need independent release cadence, or a genuinely different load profile for one module. No numeric
threshold is invented here; the trigger is an organisational or load fact, not a line count.

## Türkçe öğrenme notu

**Akış:** Modül sınırı derleme zamanında değil, test zamanında korunuyor.

**Neden:** Aynı assembly içinde `internal` modül sınırı sağlamaz; sınırı ancak bir bağımlılık
testi koruyabilir.

**Alternatif:** Ayrı assembly'ler. Sınırı derleyici korur. Bunun bedeli tek transaction veya tek
migration seti **değildir** — ayrı assembly'ler yine tek process'te, tek veritabanına, tek
`DbContext` ve tek migration seti ile deploy edilebilir. Gerçek bedel farklı: proje/paket
topolojisi büyür, ortak tipler için üçüncü bir paylaşılan assembly gerekir, `DbContext` iki
assembly'nin mapping'ini birleştirdiği için yine bir istisna noktası kalır ve sürüm/referans
yönetimi bu boyuttaki bir problem için kazandırdığından fazlasını götürür. Tek transaction'ı
kaybettiren şey ayrı assembly değil, ayrı **servis** ve ayrı veritabanıdır (bkz. tablodaki üçüncü
seçenek).

**Hata senaryosu:** Sınır testini `GetReferencedAssemblies()` ile yazmak. Aynı assembly içindeki
ihlalleri hiç görmez.

**Kod yolu:** [`ModuleBoundaryTests.cs`](../../tests/B2B.Ordering.Tests/ModuleBoundaryTests.cs)

**Deney komutu:** `dotnet test --filter-class "*ModuleBoundaryTests"`
