# Kod okuma rehberi (Türkçe)

Bu rehber altı oturuma bölünmüştür. Her oturumda okunacak kod yolları, izlenecek çağrı zinciri,
çalıştırılacak komut, beklenen davranış ve cevap anahtarlı iki düşünme sorusu var.

Ön koşul: `dotnet tool restore`, `dotnet restore --locked-mode`, çalışan Docker.

---

## Oturum 1 — Request → DTO → handler → EF → response

**Okunacak kod**

- [`Modules/Ordering/OrderEndpoints.cs`](../../src/B2B.Ordering.Api/Modules/Ordering/OrderEndpoints.cs)
- [`Modules/Ordering/Create/CreateOrderRequest.cs`](../../src/B2B.Ordering.Api/Modules/Ordering/Create/CreateOrderRequest.cs)
- [`Modules/Ordering/Create/CreateOrderHandler.cs`](../../src/B2B.Ordering.Api/Modules/Ordering/Create/CreateOrderHandler.cs) → `BuildOrderAsync`
- [`Shared/Persistence/SeedData.cs`](../../src/B2B.Ordering.Api/Shared/Persistence/SeedData.cs)

**Çağrı zinciri:** `CreateAsync` (endpoint) → `CreateOrderHandler.HandleAsync` →
`RequestNormalizer.Normalize` → `BuildOrderAsync` → `Products` + `CompanyProductPrices` sorgusu →
`Serialize`.

**Deney:** Şirkete özel fiyat ve snapshot.

```
dotnet test --filter-class "*OrderTests"
```

**Beklenen davranış:** `SKU-001` liste fiyatı 100 TRY. A şirketi için anlaşmalı fiyat 80 TRY, yani
2 adet = 160 TRY. B şirketi aynı ürünü 200 TRY'ye alır. Ürün fiyatı sonradan 999'a çekilse bile A
siparişi 160 kalır (`PriceSnapshotDoesNotChange`).

**Düşünme soruları**

1. İstemci gövdede `unitPrice` gönderse ne olur?
   *Cevap:* İstek reddedilir. `UnmappedMemberHandling.Disallow` bilinmeyen alanı 400 yapar; DTO'da
   böyle bir alan yok, fiyat sunucuda `BuildOrderAsync` içinde seçilir.
2. Satır fiyatını `OrderLine.UnitPrice` olarak saklamak yerine okuma anında `Product.ListPrice`
   üzerinden hesaplasak ne bozulur?
   *Cevap:* Fiyat değişince eski siparişlerin toplamı değişir; fatura ile sipariş tutmaz.
   `PriceSnapshotDoesNotChange` kırmızıya döner.

---

## Oturum 2 — JWT → membership → tenant filter → write guard

**Okunacak kod**

- [`Shared/Auth/AuthenticationSetup.cs`](../../src/B2B.Ordering.Api/Shared/Auth/AuthenticationSetup.cs)
- [`Shared/Tenancy/CompanyContextFilter.cs`](../../src/B2B.Ordering.Api/Shared/Tenancy/CompanyContextFilter.cs)
- [`Shared/Tenancy/CallerContext.cs`](../../src/B2B.Ordering.Api/Shared/Tenancy/CallerContext.cs)
- [`Shared/Persistence/AppDbContext.cs`](../../src/B2B.Ordering.Api/Shared/Persistence/AppDbContext.cs) → `GuardTenantWrites`

**Çağrı zinciri:** JwtBearer doğrulaması → `OnTokenValidated` (`app_user_id` GUID kontrolü) →
`CompanyContextFilter` → `CompanyAccessResolver.ResolveAsync` → `CallerContext.Initialize` →
query filter `TenantCompanyId` → `SaveChanges` koruması.

**Deney:** Header ile başka kullanıcı/şirket denemesi.

```
dotnet test --filter-class "*AuthenticationTests"
dotnet test --filter-class "*AuthenticationStartupTests"
dotnet test --filter-class "*IsolationTests"
dotnet test --filter-class "*SchemaConstraintTests"
```

**Beklenen davranış:** A token'ı + `X-Company-Id: B` → 403. Üstüne `X-User-Id: B kullanıcısı`
eklemek sonucu değiştirmez. B'nin sipariş ID'si A tarafından sorgulanınca 403 değil **404** döner.
`AuthenticationStartupTests` ise HTTP'ye hiç çıkmaz: yerel demo auth kipiyle Production/Staging
başlangıcının reddedildiğini, Development'ın çalıştığını gösterir ve veritabanı istemez.

**Düşünme soruları**

1. `CallerContext` başlatılmadan `db.Orders` sorgulanırsa ne olur, neden böyle tasarlandı?
   *Cevap:* `InvalidOperationException` fırlar (`TenantQueryWithoutContextFails`). Alternatif olan
   "boş şirket = filtre yok" davranışı, bağlam kurulmamış bir kod yolunda bütün şirketleri açardı.
2. Ortam kontrolü neden `Jwt:SigningKey` dolu olduğunda değil, koşulsuz çalışıyor?
   *Cevap:* Anahtar tek yoldan gelmiyor. `dotnet user-jwts` onu
   `Authentication:Schemes:Bearer:SigningKeys` altına yazar ve framework kendisi bağlar; hiç
   anahtar verilmediğinde de yerleşik demo issuer/audience yürürlükte kalır. Guard'ı tek
   configuration yoluna bağlamak, diğer iki durumda uygulamanın Production'da sessizce
   başlamasına izin verir. Doğrusu: desteklenen kipi adıyla tanımlamak ve ortam dışında
   başlangıcı reddetmek.
3. Query filter ve `SaveChanges` koruması varken foreign key'lere neden hâlâ ihtiyaç var?
   *Cevap:* İkisi de entity'nin şirketini **çağıranın** şirketiyle karşılaştırır; şirketin
   *yanlış* olduğunu anlayabilir ama *hiç var olmadığını* anlayamaz. `Orders` tablosundaki
   `Companies`/`Users` FK'leri olmayan şirkete bağlı sipariş yazılmasını engeller
   (`SchemaConstraintTests`). `CrossTenantWriteRejected` uygulama korumasını,
   `CrossTenantLineForeignKeyRejected` ise composite FK'yi gösterir.

---

## Oturum 3 — ProblemDetails ve hata yolları

**Okunacak kod**

- [`Shared/Errors/ErrorCodes.cs`](../../src/B2B.Ordering.Api/Shared/Errors/ErrorCodes.cs)
- [`Shared/Errors/ApiException.cs`](../../src/B2B.Ordering.Api/Shared/Errors/ApiException.cs)
- [`Shared/Errors/ApiExceptionHandler.cs`](../../src/B2B.Ordering.Api/Shared/Errors/ApiExceptionHandler.cs)
- [`Shared/Errors/ProblemDetailsSetup.cs`](../../src/B2B.Ordering.Api/Shared/Errors/ProblemDetailsSetup.cs)
- [`Shared/Errors/ProblemResponseWriter.cs`](../../src/B2B.Ordering.Api/Shared/Errors/ProblemResponseWriter.cs)

**Çağrı zinciri:** Üç kapı — `ApiException` → handler; beklenmeyen exception → handler + log;
exception üretmeyen statüler (401/403/404/405/415) → `UseApiStatusCodeProblems`. Üçü de gövdeyi
`ProblemResponseWriter` üzerinden yazar.

**Deney:** Malformed JSON ile iş hatasını karşılaştır.

```
dotnet test --filter-class "*ErrorContractTests"
```

**Beklenen davranış:** İkisi de `application/problem+json`, ikisi de `errorCode` ve `traceId`
taşır; ama biri `malformed_request`, diğeri `validation_failed` ve `errors` alanı içerir.

**Düşünme soruları**

1. Sadece `app.UseExceptionHandler()` yazsaydık hangi senaryolar sözleşmenin dışında kalırdı?
   *Cevap:* Exception üretmeyen her şey: JwtBearer challenge (401), forbid (403), eşleşmeyen route
   (404), yanlış method (405), yanlış content type (415).
2. `Accept: application/xml` gönderen bir istemci hata gövdesini neden yine de alıyor?
   *Cevap:* `IProblemDetailsService` içerik pazarlığı yapar ve JSON kabul edilmiyorsa yazmayı
   reddeder. Başarı gövdesi için makul, hata için değil: istemci sebepsiz bir statü kodu ile
   kalır. `ProblemResponseWriter` önce servise şans verir, reddedilirse gövdeyi kendisi
   `application/problem+json` olarak yazar.
3. Neden her hatalı girdi için ayrı exception sınıfı yok?
   *Cevap:* Statü, kod ve alan hataları veri olarak taşınıyor. Yeni bir hata durumu yeni bir sınıf
   değil, `ErrorCodes` içine yeni bir sabit gerektiriyor — ve bu bilinçli bir sözleşme değişikliği.

---

## Oturum 4 — Unique index → transaction → replay

**Okunacak kod**

- [`Modules/Ordering/Create/RequestNormalizer.cs`](../../src/B2B.Ordering.Api/Modules/Ordering/Create/RequestNormalizer.cs)
- [`Modules/Ordering/Create/CreateOrderHandler.cs`](../../src/B2B.Ordering.Api/Modules/Ordering/Create/CreateOrderHandler.cs) → `CreateAsync`, `ReplayAsync`
- [`Modules/Ordering/Idempotency/IdempotencyStore.cs`](../../src/B2B.Ordering.Api/Modules/Ordering/Idempotency/IdempotencyStore.cs)
- [ADR 0003](../decisions/0003-idempotency.md)

**Çağrı zinciri:** normalize + hash → `BEGIN TRAN` → rezervasyon INSERT → sipariş → yanıtı bir kez
serialize et → kaydı tamamla → `COMMIT`. Unique ihlali → rollback → yeni context → replay veya 409.

**Deney:** 10 istek, rollback ve kayıp yanıt.

```
dotnet test --filter-class "*IdempotencyTests"
```

**Beklenen davranış:** 10 eşzamanlı aynı istek → hepsi 201, aynı gövde, **tek** sipariş ve
tamamlanmış tek kayıt. Commit öncesi hata → ne sipariş ne rezervasyon kalır, tekrar deneme temiz
çalışır. Commit sonrası yanıt kaybı → sipariş vardır, sonraki istek kaydedilmiş yanıtı alır.

**Düşünme soruları**

1. JSON'da boşlukları değiştirmek replay üretirken satırların sırasını değiştirmek neden 409?
   *Cevap:* Hash ham byte'lar üzerinden değil, normalize edilmiş DTO üzerinden alınıyor. Boşluk ve
   property sırası anlamı değiştirmez; satır sırası ise korunan bir bilgi, yani farklı bir istek.
2. Unique ihlalinden sonra aynı `DbContext` ile okuma yapsak ne olur?
   *Cevap:* Context failed state'te ve eski tracked entity'ler duruyor; ikinci `SaveChanges`
   beklenmedik davranır. Bu yüzden `IdempotencyStore` yeni bir context açıyor.

---

## Oturum 5 — Modül sınırı ve ortak DbContext

**Okunacak kod**

- [`tests/.../ModuleBoundaryTests.cs`](../../tests/B2B.Ordering.Tests/ModuleBoundaryTests.cs)
- [`Modules/Access/Contracts/ICompanyAccessResolver.cs`](../../src/B2B.Ordering.Api/Modules/Access/Contracts/ICompanyAccessResolver.cs)
- [ADR 0001](../decisions/0001-module-boundaries.md)

**Deney:** Mimari testi bozacak bağımlılığı açıklama.

```
dotnet test --filter-class "*ModuleBoundaryTests"
```

Denemek isterseniz: `ListOrdersHandler.HandleAsync` gövdesine
`var probe = new B2B.Ordering.Api.Modules.Access.Data.Company();` satırını ekleyin, testi
çalıştırın, sonra satırı silin. Test `OrderingDependsOnAccessOnlyThroughContracts` adıyla kırmızı
olmalı ve ihlal eden tipi adıyla söylemeli.

**Beklenen davranış:** İhlal metod gövdesinde olsa bile yakalanır, çünkü NetArchTest IL okur.

**Düşünme soruları**

1. `internal` anahtar sözcüğü bu sınırı sağlar mıydı?
   *Cevap:* Hayır. Aynı assembly içinde `internal` her yerden görünür; sınır ancak bir bağımlılık
   testiyle korunur.
2. `AppDbContext` istisnası neden tek tek isimle listelendi, namespace olarak değil?
   *Cevap:* Bütün `Shared` namespace'ini istisna yapmak, sınırın anlamını yok ederdi. İstisna dar
   tutulunca yeni bir Shared tipi Access'e sızdığında test uyarır. Tam liste üç maddedir:
   `AppDbContext` ve `SeedData` isimle, bir de `Shared.Persistence.Configurations` namespace'i —
   yani EF mapping sınıfları. Namespace olarak muaf tutulan tek yer orasıdır ve yalnız mapping
   bildirimlerini kapsar (bkz. ADR 0001).

---

## Oturum 6 — V1/V2/V3 ve backfill

**Okunacak kod**

- [`Migrations/`](../../src/B2B.Ordering.Api/Migrations/) → S1, S2 (expand), S2B (backfill kapısı),
  S3 (contract)
- [`samples/Migration.V1`](../../samples/Migration.V1/Program.cs),
  [`samples/Migration.V2`](../../samples/Migration.V2/Program.cs)
- [ADR 0005](../decisions/0005-migration-compatibility.md)

**Deney:** Contract öncesi/sonrası geri dönüş sınırı.

```
dotnet test --filter-class "*MigrationCompatibilityTests"
pwsh -File scripts/migration-demo.ps1
```

**Beklenen davranış:** S2'de V1 ve V2 aynı DB üzerinde ayrı process olarak çalışır. Expand'den
sonra V1'in yazdığı satır, **S2B backfill adımı çalışana kadar** V3 tarafından null okunur; demo bu
pencereyi açıkça gösterir. S3 sonrası V1 ve V2 beklenen SQL hatasını verir ve bu hata test
tarafından **doğrulanır**, kırmızı bırakılmaz.

**Düşünme soruları**

1. Son backfill neden ayrı bir migration (S2B) olarak duruyor, expand'in içine konmuyor?
   *Cevap:* Expand yalnız o an var olan satırları kopyalar. V1 ayakta kaldığı sürece sadece eski
   kolona yazmaya devam eder ve V3 yalnız yeni kolonu okur. S2B, "son V1/V2 yazıcısı durdu" ile
   "V3 açılabilir" arasındaki kapı; olmazsa V3 aradaki referansları null görür.
2. S2B neden yalnız kopyalamıyor, bir de doğruluyor?
   *Cevap:* Kapı, V3'ü açan adım. İki kolonda farklı iki değer kalmışsa kopyalama bunları
   düzeltmez (kopyalama yalnız NULL olanları doldurur) ve kapı sessizce "tamam" der. S2B
   kopyaladıktan sonra uyumu doğrular, uyuşmazlıkta migration'ı düşürür ve iki kolonu da yerinde
   bırakır. Karşılaştırma binary collation ile yapılır; aksi hâlde `PO-Ref` ile `PO-REF` eşit
   sayılır ve gerçek bir fark kaybolur. V3'ün yazdığı (eski kolonu null) satırlar uyuşmazlık
   değildir.
3. V3, S2 üzerinde yalnız yeni kolona yazdıktan sonra hangi eski sürüm null okur?
   *Cevap:* Yalnız V1. V2 önce yeni kolonu okuduğu için reverse backfill'den önce de değeri
   görür. Reverse backfill yine de gerekir: eski kolon V1 uyumluluğu ve S2B/S3 uyum kapısı için
   doldurulmalıdır.
4. EF'in ürettiği `RenameColumn` migration'ı neden elle `AddColumn` + backfill'e çevrildi?
   *Cevap:* Rename atomik olarak eski kolonu yok eder; migration'ın çalıştığı anda ayakta olan her
   V1 instance'ı kırılır. Expand/contract, silmeyi tüm eski yazıcılar durduktan sonraya erteler.
3. Contract sonrası eski binary'ye dönmek neden desteklenmiyor?
   *Cevap:* `Down()` kolonu yeniden yaratıp yeni kolondan kopyalar; bu bir şema onarımıdır. Sadece
   silinen kolonda var olmuş bir değer geri gelmez.
