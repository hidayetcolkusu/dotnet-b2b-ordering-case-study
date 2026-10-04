using System.Text.Json;
using B2B.Ordering.Api.Modules.Access.Contracts;
using B2B.Ordering.Api.Modules.Ordering.Data;
using B2B.Ordering.Api.Modules.Ordering.Idempotency;
using B2B.Ordering.Api.Shared.Errors;
using B2B.Ordering.Api.Shared.Persistence;
using B2B.Ordering.Api.Shared.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Api.Modules.Ordering.Create;

/// <summary>
/// Creates one order, exactly once per idempotency key.
///
/// The coordination lives here rather than in an endpoint filter because the reservation row and
/// the order must share one transaction: a committed reservation always carries a replayable
/// response, and a rolled back attempt leaves neither behind.
/// </summary>
public sealed class CreateOrderHandler(
    AppDbContext db,
    CallerContext caller,
    IdempotencyStore store,
    ICreateOrderFaultHook faults,
    TimeProvider clock,
    IdempotencyOptions options)
{
    public static readonly JsonSerializerOptions ResponseJson = new(JsonSerializerDefaults.Web);

    public async Task<StoredResponse> HandleAsync(
        CreateOrderRequest? request,
        string key,
        CancellationToken ct)
    {
        // Authorisation is re-checked on every attempt, replays included: a user whose membership
        // was revoked must not be handed the stored response.
        if (!string.Equals(caller.Role, CompanyRoles.Buyer, StringComparison.Ordinal))
        {
            throw ApiException.RoleNotAllowed(CompanyRoles.Buyer);
        }

        var normalizedKey = RequestNormalizer.NormalizeIdempotencyKey(key);
        var normalized = RequestNormalizer.Normalize(request);
        var hash = RequestNormalizer.Hash(normalized);

        try
        {
            return await CreateAsync(normalizedKey, hash, normalized, ct);
        }
        catch (Exception exception) when (IdempotencyStore.IsIdempotencyUniqueViolation(exception))
        {
            return await ReplayAsync(normalizedKey, hash, ct);
        }
        catch (Exception exception) when (IdempotencyStore.IsLockTimeout(exception))
        {
            // Another attempt with the same key still holds the reservation lock.
            throw ApiException.IdempotencyBusy();
        }
    }

    private async Task<StoredResponse> CreateAsync(
        string key,
        string hash,
        NormalizedOrderRequest normalized,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        // Bounded waiting: a request must fail fast with 503 rather than hold a connection while
        // a competing attempt finishes.
        // SET LOCK_TIMEOUT does not accept a parameter, so the value is clamped to a plain integer
        // before it is formatted into the statement. It never comes from request input.
        var lockTimeout = Math.Clamp(options.LockTimeoutMilliseconds, 0, 60_000);
#pragma warning disable EF1002, EF1003 // The value is an int clamped on the line above.
        await db.Database.ExecuteSqlRawAsync(
            "SET LOCK_TIMEOUT " + lockTimeout.ToString(System.Globalization.CultureInfo.InvariantCulture) + ";",
            ct);
#pragma warning restore EF1002, EF1003

        var reservation = new IdempotencyRecord
        {
            Id = Guid.CreateVersion7(),
            CompanyId = caller.CompanyId,
            UserId = caller.UserId,
            Key = key,
            RequestHash = hash,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
        };

        db.IdempotencyRecords.Add(reservation);
        await db.SaveChangesAsync(ct);
        await faults.AfterReservationSavedAsync(ct);

        var order = await BuildOrderAsync(normalized, ct);
        db.Orders.Add(order);

        // Serialised once, inside the transaction, so a replay can never differ from the original.
        var stored = Serialize(order, normalized);

        reservation.OrderId = order.Id;
        reservation.StatusCode = stored.StatusCode;
        reservation.ResponseBody = stored.Body;
        reservation.Location = stored.Location;

        await db.SaveChangesAsync(ct);
        await faults.BeforeCommitAsync(ct);

        await transaction.CommitAsync(ct);
        await faults.AfterCommitAsync(ct);

        return stored;
    }

    /// <summary>
    /// Runs after the reservation insert lost the unique race. The failed context is abandoned and
    /// a fresh one reads the committed record.
    /// </summary>
    private async Task<StoredResponse> ReplayAsync(string key, string hash, CancellationToken ct)
    {
        var committed = await store.ReadCommittedAsync(key, ct);

        if (committed is null)
        {
            // The winner has not committed yet, so there is nothing to replay. Retrying is safe.
            throw ApiException.IdempotencyBusy();
        }

        if (!string.Equals(committed.RequestHash, hash, StringComparison.Ordinal))
        {
            throw ApiException.IdempotencyConflict();
        }

        if (committed.StatusCode is null || committed.ResponseBody is null || committed.Location is null)
        {
            // An incomplete row can only exist if a reservation was committed without its
            // response, which the single-transaction design does not allow.
            throw ApiException.IdempotencyBusy();
        }

        return new StoredResponse(
            committed.StatusCode.Value,
            committed.ResponseBody,
            committed.Location);
    }

    /// <summary>
    /// Builds the order from server-side data. Prices are snapshotted onto the lines, so a later
    /// catalogue price change cannot alter an existing order.
    /// </summary>
    internal async Task<Order> BuildOrderAsync(NormalizedOrderRequest normalized, CancellationToken ct)
    {
        var skus = normalized.Lines.Select(line => line.Sku).ToArray();

        var products = await db.Products
            .AsNoTracking()
            .Where(product => product.IsActive && skus.Contains(product.Sku))
            .ToDictionaryAsync(product => product.Sku, ct);

        var productIds = products.Values.Select(product => product.Id).ToArray();

        var prices = await db.CompanyProductPrices
            .AsNoTracking()
            .Where(price => productIds.Contains(price.ProductId))
            .ToDictionaryAsync(price => price.ProductId, price => price.UnitPrice, ct);

        var orderId = Guid.CreateVersion7();
        var order = new Order
        {
            Id = orderId,
            CompanyId = caller.CompanyId,
            CreatedByUserId = caller.UserId,
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime,
            ExternalReference = normalized.ExternalReference,
        };

        decimal total = 0m;

        foreach (var line in normalized.Lines)
        {
            if (!products.TryGetValue(line.Sku, out var product))
            {
                throw ApiException.ProductNotFound(line.Sku);
            }

            // Company specific price wins; otherwise the catalogue list price applies.
            var unitPrice = prices.TryGetValue(product.Id, out var negotiated)
                ? negotiated
                : product.ListPrice;

            var lineTotal = unitPrice * line.Quantity;
            total += lineTotal;

            if (total > Money.MaxAmount)
            {
                throw ApiException.Validation(
                    "lines",
                    "The order total exceeds the maximum amount this API can store.");
            }

            order.Lines.Add(new OrderLine
            {
                Id = Guid.CreateVersion7(),
                OrderId = orderId,
                CompanyId = caller.CompanyId,
                ProductId = product.Id,
                LineNumber = order.Lines.Count + 1,
                Quantity = line.Quantity,
                UnitPrice = unitPrice,
                LineTotal = lineTotal,
            });
        }

        order.TotalAmount = total;
        return order;
    }

    internal static StoredResponse Serialize(Order order, NormalizedOrderRequest normalized)
    {
        var lines = order.Lines
            .Select((line, index) => new OrderLineResponse(
                normalized.Lines[index].Sku,
                line.Quantity,
                line.UnitPrice,
                line.LineTotal))
            .ToArray();

        var response = new CreateOrderResponse(
            order.Id,
            order.CompanyId,
            order.TotalAmount,
            Money.Currency,
            order.CreatedAtUtc,
            order.ExternalReference,
            lines);

        return new StoredResponse(
            StatusCodes.Status201Created,
            JsonSerializer.Serialize(response, ResponseJson),
            $"/api/orders/{order.Id}");
    }
}

/// <summary>How long a create attempt may wait for a competing attempt with the same key.</summary>
public sealed class IdempotencyOptions
{
    public int LockTimeoutMilliseconds { get; set; } = 5000;
}
