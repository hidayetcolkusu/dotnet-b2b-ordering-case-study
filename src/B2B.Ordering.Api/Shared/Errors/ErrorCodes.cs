namespace B2B.Ordering.Api.Shared.Errors;

/// <summary>
/// The closed vocabulary an integrating client may switch on. Adding a code is a contract
/// change; adding a new exception class per bad input is not.
/// </summary>
public static class ErrorCodes
{
    public const string ValidationFailed = "validation_failed";
    public const string MalformedRequest = "malformed_request";
    public const string UnsupportedMediaType = "unsupported_media_type";
    public const string MethodNotAllowed = "method_not_allowed";
    public const string CompanyHeaderInvalid = "company_header_invalid";
    public const string Unauthenticated = "unauthenticated";
    public const string CompanyAccessDenied = "company_access_denied";
    public const string RoleNotAllowed = "role_not_allowed";
    public const string OrderNotFound = "order_not_found";
    public const string ProductNotFound = "product_not_found";
    public const string RouteNotFound = "route_not_found";
    public const string IdempotencyKeyMissing = "idempotency_key_missing";
    public const string IdempotencyKeyInvalid = "idempotency_key_invalid";
    public const string IdempotencyKeyConflict = "idempotency_key_conflict";
    public const string IdempotencyBusy = "idempotency_busy";
    public const string InternalError = "internal_error";
}
