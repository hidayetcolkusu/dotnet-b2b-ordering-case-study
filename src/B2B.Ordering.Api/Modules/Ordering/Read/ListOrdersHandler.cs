using B2B.Ordering.Api.Modules.Ordering.Create;
using B2B.Ordering.Api.Shared.Errors;
using B2B.Ordering.Api.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Api.Modules.Ordering.Read;

public sealed class ListOrdersHandler(AppDbContext db)
{
    public const int MaxPageSize = 100;

    public async Task<OrderListResponse> HandleAsync(int page, int pageSize, CancellationToken ct)
    {
        if (page < 1)
        {
            throw ApiException.Validation("page", "Page must be 1 or greater.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            throw ApiException.Validation("pageSize", $"Page size must be between 1 and {MaxPageSize}.");
        }

        var query = db.Orders.AsNoTracking();

        var totalCount = await query.CountAsync(ct);

        // Computed in long: (page - 1) * pageSize overflows int well before a caller could reach a
        // real page, and a wrapped negative offset would reach the database as a broken query.
        var skip = ((long)page - 1) * pageSize;

        if (skip >= totalCount)
        {
            // Past the end is an empty page, not an error: the caller asked a valid question and
            // the answer is "nothing here". totalCount still tells them where the data ends.
            return new OrderListResponse([], page, pageSize, totalCount);
        }

        // CreatedAtUtc then Id: two orders created in the same tick still have one stable order,
        // so paging cannot show or skip a row twice.
        var items = await query
            .OrderBy(order => order.CreatedAtUtc)
            .ThenBy(order => order.Id)
            .Skip((int)skip)
            .Take(pageSize)
            .Select(OrderProjection.Expression)
            .ToListAsync(ct);

        return new OrderListResponse(items, page, pageSize, totalCount);
    }
}
