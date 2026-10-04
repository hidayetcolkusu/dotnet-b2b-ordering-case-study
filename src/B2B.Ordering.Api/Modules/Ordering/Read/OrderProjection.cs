using System.Linq.Expressions;
using B2B.Ordering.Api.Modules.Ordering.Create;
using B2B.Ordering.Api.Modules.Ordering.Data;

namespace B2B.Ordering.Api.Modules.Ordering.Read;

/// <summary>
/// One projection shared by the detail and list reads, so both endpoints always return the same
/// shape and neither can accidentally serialise an EF entity.
/// </summary>
internal static class OrderProjection
{
    public static readonly Expression<Func<Order, CreateOrderResponse>> Expression =
        order => new CreateOrderResponse(
            order.Id,
            order.CompanyId,
            order.TotalAmount,
            Money.Currency,
            order.CreatedAtUtc,
            order.ExternalReference,
            order.Lines
                .OrderBy(line => line.LineNumber)
                .Select(line => new OrderLineResponse(
                    line.Product.Sku,
                    line.Quantity,
                    line.UnitPrice,
                    line.LineTotal))
                .ToList());
}
