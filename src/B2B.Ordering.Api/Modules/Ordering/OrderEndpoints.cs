using B2B.Ordering.Api.Modules.Access.Contracts;
using B2B.Ordering.Api.Modules.Ordering.Create;
using B2B.Ordering.Api.Modules.Ordering.Read;
using B2B.Ordering.Api.Shared.Errors;
using B2B.Ordering.Api.Shared.Tenancy;

namespace B2B.Ordering.Api.Modules.Ordering;

public static class OrderEndpoints
{
    public const string IdempotencyHeader = "Idempotency-Key";

    private const string ProblemJson = "application/problem+json";

    public static IEndpointRouteBuilder MapOrderEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/orders")
            .RequireAuthorization()
            .AddEndpointFilter<CompanyContextFilter>()
            .WithTags("Orders");

        // The tenant headers are documented by TenantHeaderTransformer, because the endpoint filter
        // reads them instead of binding them as parameters.
        group.MapPost("/", CreateAsync)
            .WithName("CreateOrder")
            .WithSummary("Create an order")
            .WithDescription(
                "Buyer only. Prices, totals, company and creating user are decided on the server; "
                + "the request carries only SKUs, quantities and an optional external reference. "
                + "Repeating the same Idempotency-Key with the same payload replays the stored "
                + "response and creates no second order.")
            .Produces<CreateOrderResponse>(StatusCodes.Status201Created)
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status409Conflict, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status415UnsupportedMediaType, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status503ServiceUnavailable, ProblemJson);

        group.MapGet("/{id:guid}", GetAsync)
            .WithName("GetOrder")
            .WithSummary("Read one order")
            .WithDescription(
                "Buyer or Viewer. An order belonging to another company returns 404 rather than "
                + "403, so its existence is not disclosed.")
            .Produces<CreateOrderResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status404NotFound, ProblemJson);

        group.MapGet("/", ListAsync)
            .WithName("ListOrders")
            .WithSummary("List this company's orders")
            .WithDescription(
                "Buyer or Viewer. Ordered by CreatedAtUtc then Id, so paging is stable even for "
                + "orders created in the same tick.")
            .Produces<OrderListResponse>()
            .Produces<ApiProblemDetails>(StatusCodes.Status400BadRequest, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status401Unauthorized, ProblemJson)
            .Produces<ApiProblemDetails>(StatusCodes.Status403Forbidden, ProblemJson);

        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        CreateOrderRequest? request,
        HttpContext http,
        CreateOrderHandler handler,
        CancellationToken ct)
    {
        var key = http.Request.Headers.TryGetValue(IdempotencyHeader, out var values)
            ? values.ToString()
            : null;

        var stored = await handler.HandleAsync(request, key ?? string.Empty, ct);

        // The stored body is written back verbatim; it is never deserialised and re-serialised.
        http.Response.Headers.Location = stored.Location;
        return Results.Content(stored.Body, "application/json", statusCode: stored.StatusCode);
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        CallerContext caller,
        GetOrderHandler handler,
        CancellationToken ct)
    {
        caller.RequireReadAccess();
        return Results.Ok(await handler.HandleAsync(id, ct));
    }

    private static async Task<IResult> ListAsync(
        CallerContext caller,
        ListOrdersHandler handler,
        CancellationToken ct,
        int page = 1,
        int pageSize = 20)
    {
        caller.RequireReadAccess();
        return Results.Ok(await handler.HandleAsync(page, pageSize, ct));
    }

    /// <summary>Both roles may read; only Buyer may create.</summary>
    internal static void RequireReadAccess(this CallerContext caller)
    {
        if (!CompanyRoles.IsKnown(caller.Role))
        {
            throw ApiException.RoleNotAllowed(CompanyRoles.Viewer);
        }
    }
}
