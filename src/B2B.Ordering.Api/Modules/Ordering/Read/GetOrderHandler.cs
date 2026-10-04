using B2B.Ordering.Api.Modules.Ordering.Create;
using B2B.Ordering.Api.Shared.Errors;
using B2B.Ordering.Api.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Api.Modules.Ordering.Read;

/// <summary>
/// Reads one order. There is no company predicate in this query on purpose: the tenant query
/// filter supplies it, so another company's order is simply not there and the caller gets a 404.
/// </summary>
public sealed class GetOrderHandler(AppDbContext db)
{
    public async Task<CreateOrderResponse> HandleAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders
            .AsNoTracking()
            .Where(o => o.Id == orderId)
            .Select(OrderProjection.Expression)
            .SingleOrDefaultAsync(ct);

        return order ?? throw ApiException.OrderNotFound();
    }
}
