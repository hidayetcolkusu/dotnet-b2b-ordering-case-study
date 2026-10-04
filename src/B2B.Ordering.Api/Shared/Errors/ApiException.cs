using System.Net;

namespace B2B.Ordering.Api.Shared.Errors;

/// <summary>
/// The single exception type for expected business failures. Its status, error code and
/// optional field errors are all data, so new failures never need a new class.
/// </summary>
public sealed class ApiException(
    HttpStatusCode status,
    string errorCode,
    string title,
    string detail,
    IReadOnlyDictionary<string, string[]>? errors = null)
    : Exception(detail)
{
    public HttpStatusCode Status { get; } = status;
    public string ErrorCode { get; } = errorCode;
    public string Title { get; } = title;
    public string Detail { get; } = detail;
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;

    public static ApiException Validation(string detail, IReadOnlyDictionary<string, string[]>? errors = null) =>
        new(HttpStatusCode.BadRequest, ErrorCodes.ValidationFailed, "Validation failed", detail, errors);

    public static ApiException Validation(string field, string message) =>
        Validation("The request body failed validation.",
            new Dictionary<string, string[]> { [field] = [message] });

    public static ApiException CompanyHeader(string detail) =>
        new(HttpStatusCode.BadRequest, ErrorCodes.CompanyHeaderInvalid,
            "Company header invalid", detail);

    public static ApiException CompanyAccessDenied() =>
        new(HttpStatusCode.Forbidden, ErrorCodes.CompanyAccessDenied,
            "Company access denied",
            "The authenticated user has no active membership in the requested company.");

    public static ApiException RoleNotAllowed(string requiredRole) =>
        new(HttpStatusCode.Forbidden, ErrorCodes.RoleNotAllowed, "Role not allowed",
            $"This operation requires the {requiredRole} role in the requested company.");

    public static ApiException OrderNotFound() =>
        new(HttpStatusCode.NotFound, ErrorCodes.OrderNotFound, "Order not found",
            "No order with this identifier exists for the requested company.");

    public static ApiException ProductNotFound(string sku) =>
        new(HttpStatusCode.NotFound, ErrorCodes.ProductNotFound, "Product not found",
            $"No active product exists with SKU '{sku}'.");

    public static ApiException IdempotencyConflict() =>
        new(HttpStatusCode.Conflict, ErrorCodes.IdempotencyKeyConflict, "Idempotency key conflict",
            "This idempotency key was already used with a different request payload.");

    public static ApiException IdempotencyBusy() =>
        new(HttpStatusCode.ServiceUnavailable, ErrorCodes.IdempotencyBusy, "Request is being processed",
            "Another request with the same idempotency key is still in progress. Retry shortly.");
}
