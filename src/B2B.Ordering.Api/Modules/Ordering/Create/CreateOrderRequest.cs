namespace B2B.Ordering.Api.Modules.Ordering.Create;

/// <summary>
/// The wire contract. The client never sends a price, a total, a company or a user: those are
/// server decisions, so they are simply not part of the request.
/// </summary>
public sealed record CreateOrderRequest(
    IReadOnlyList<CreateOrderLineRequest>? Lines,
    string? ExternalReference);

public sealed record CreateOrderLineRequest(string? Sku, int Quantity);

/// <summary>
/// Response DTO. EF entities are never serialised directly, so the persistence model can change
/// (as it does in the migration experiment) without changing the HTTP contract.
/// </summary>
public sealed record CreateOrderResponse(
    Guid Id,
    Guid CompanyId,
    decimal TotalAmount,
    string Currency,
    DateTime CreatedAtUtc,
    string? ExternalReference,
    IReadOnlyList<OrderLineResponse> Lines);

public sealed record OrderLineResponse(
    string Sku,
    int Quantity,
    decimal UnitPrice,
    decimal LineTotal);

public sealed record OrderListResponse(
    IReadOnlyList<CreateOrderResponse> Items,
    int Page,
    int PageSize,
    int TotalCount);

/// <summary>Everything in this contract is priced in Turkish lira; tax and discounts are out of scope.</summary>
public static class Money
{
    public const string Currency = "TRY";

    /// <summary>The largest value decimal(18,2) can hold. Exceeding it is a validation error.</summary>
    public const decimal MaxAmount = 9999999999999999.99m;
}

