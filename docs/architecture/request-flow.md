# Request flow: POST /api/orders

```
  client
    │  POST /api/orders
    │  Authorization: Bearer <jwt>
    │  X-Company-Id: <guid>
    │  Idempotency-Key: <key>
    ▼
┌───────────────────────────────────────────────────────────────────────────┐
│ UseExceptionHandler        every exception → one ProblemDetails shape     │
│ UseStatusCodePages         401/403/404/405/415 → the same shape           │
│ UseRouting                                                                │
│ UseAuthentication          JwtBearer: signature, issuer, audience,        │
│                            lifetime, then app_user_id must be a GUID      │
│ UseAuthorization           no identity → 401 (company header not read yet) │
└───────────────────────────────────────────────────────────────────────────┘
    │
    ▼
CompanyContextFilter (endpoint filter, order endpoints only)
    │  X-Company-Id parsed          → 400 if missing or not a GUID
    │  ICompanyAccessResolver       → 403 if user, company or membership inactive
    │  CallerContext.Initialize     → write-once; role comes from the database
    ▼
CreateOrderHandler
    │  role must be Buyer           → 403        (re-checked on every attempt)
    │  normalize key + payload      → 400
    │  SHA-256 over normalised DTO
    │
    │  BEGIN TRANSACTION
    │    SET LOCK_TIMEOUT <n>
    │    INSERT IdempotencyRecord (CompanyId, UserId, Key)  ◄── unique index
    │    load products + company prices
    │    build order and lines, snapshot prices
    │    INSERT Order + OrderLines
    │    serialise the 201 response ONCE
    │    complete the record with OrderId / status / body / Location
    │  COMMIT
    ▼
201 Created + Location, body written from the stored bytes
```

## The three ways out of the transaction

| What happened | What the caller sees | What the database holds |
|---|---|---|
| Everything committed | 201 with the serialised body | one order, one completed record |
| Unique violation on the reservation INSERT | 201 replay, or 409 if the hash differs | one order, one completed record |
| Any failure before COMMIT | 500 (or the mapped business error) | nothing: no order, no reservation |
| Lock wait exceeded | 503 with `Retry-After: 1` | nothing |

After a unique violation the failed context is abandoned: it is never handed back to
`SaveChanges`. A brand new `AppDbContext` reads the committed record, scoped to company **and**
user **and** key, because that is exactly what the unique index covers.

Only a unique violation on `UX_IdempotencyRecords_Company_User_Key` is treated as a duplicate.
Every other database error is rethrown, so a genuine fault is never swallowed as "already done".

## Read flow

`GET /api/orders/{id}` and `GET /api/orders` run the same first half. The queries carry no company
predicate of their own: the tenant query filter supplies it. That is why another company's order
returns **404** rather than 403 — the caller must not even learn that it exists.

## Türkçe öğrenme notu

**Akış:** JWT → membership → CallerContext → rol → idempotency rezervasyonu → sipariş transaction'ı
→ kaydedilmiş yanıt.

**Neden:** Rezervasyon ile siparişin aynı transaction'da olması, "commit edilmiş kayıt her zaman
tekrar oynatılabilir yanıt taşır" garantisini veriyor. Ayrı adımlar olsaydı yarı yazılmış kayıt
kalabilirdi.

**Alternatif:** Endpoint filter içinde idempotency. Filtre transaction'a giremediği için sipariş
ile rezervasyonu atomik bağlayamaz; bu yüzden handler içinde açık koordinasyon seçildi.

**Hata senaryosu:** Unique ihlalinden sonra aynı `DbContext` ile devam etmek. EF context'i failed
state'te olduğu için ikinci `SaveChanges` beklenmedik hata verir.

**Kod yolu:**
[`CreateOrderHandler.cs`](../../src/B2B.Ordering.Api/Modules/Ordering/Create/CreateOrderHandler.cs)

**Deney komutu:** `dotnet test --filter-class "*IdempotencyTests"`
