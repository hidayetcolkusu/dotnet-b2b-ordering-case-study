# ADR 0004 — One error contract for every supported request

**Status:** Accepted

## Context

An integrating team needs to branch on failures without parsing prose. `app.UseExceptionHandler()`
alone does not give that: a 401 from JwtBearer, a 404 from routing and a 415 from model binding
never reach an exception handler.

## Decision

Every supported request that fails answers with `application/problem+json` (RFC 9457) carrying
`type`, `title`, `status`, `instance`, `errorCode`, `traceId`, plus `errors` for field level
validation failures.

`errorCode` is a **closed vocabulary** in
[`ErrorCodes`](../../src/B2B.Ordering.Api/Shared/Errors/ErrorCodes.cs). Adding one is a contract
change. There is a single `ApiException` type whose status, code and field errors are data — a new
bad input never needs a new exception class.

Three doors are wired to the same body:

| Door | Wired by |
|---|---|
| Business failures (`ApiException`) | `ApiExceptionHandler` |
| Unexpected exceptions | `ApiExceptionHandler`, logged in full, reported as a bare 500 |
| Statuses produced without an exception (401 challenge, 403 forbid, 404 route, 405, 415) | `AddProblemDetails` + `UseStatusCodePages` |

Minimal API binding failures arrive as `BadHttpRequestException` and are mapped to 400, or 415 for
a wrong content type. The framework message is **not** echoed, because it can quote payload
fragments; a fixed description is returned instead.

**Content negotiation must not cost the caller the reason.** `IProblemDetailsService` negotiates,
and simply declines to write when the request accepts neither JSON nor `*/*` — leaving an
integrator with a bare status code, no `errorCode` and no `traceId`. Declining is defensible for a
success body; for an error it is not. The service is still given first refusal, so the shared
customization applies, and `ProblemResponseWriter` takes over when it declines and writes
`application/problem+json` anyway. Both doors use it, because the two paths fail differently:
the exception path dropped the body entirely, while `UseStatusCodePages` fell back to a plain-text
line.

Unexpected errors never leak internals. A test injects a fault carrying a recognisable string and
asserts that neither the string nor the exception type name appears in the response.

## Status codes

400 validation, binding, headers · 401 authentication · 403 membership and role ·
404 product, order, route · 405 method · 409 same key, different payload · 415 media type ·
500 unexpected · 503 idempotency wait exceeded, with `Retry-After: 1`.

Malformed HTTP that the web server rejects before the application sees it is out of scope.

## Türkçe öğrenme notu

**Akış:** Hata → hangi kapıdan çıktığına bakılmaksızın aynı ProblemDetails gövdesi.

**Neden:** Entegrasyon yapan ekip `errorCode` alanına bakarak dallanabilsin; metin değişince
istemci kırılmasın.

**Alternatif:** Her hata tipi için ayrı exception sınıfı. Sınıf sayısı artar, sözleşme değişmez.

**Hata senaryosu:** Sadece `UseExceptionHandler()` yazıp 401/404/415'in de aynı gövdeyi ürettiğini
varsaymak.

**Kod yolu:** [`Shared/Errors/`](../../src/B2B.Ordering.Api/Shared/Errors/)

**Deney komutu:** `dotnet test --filter-class "*ErrorContractTests"`
