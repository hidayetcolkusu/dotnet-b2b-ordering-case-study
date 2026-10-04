using System.Net.Http.Json;
using System.Text.Json;
using B2B.Ordering.Api.Modules.Ordering.Create;

namespace B2B.Ordering.Tests.Support;

/// <summary>Small builders so the tests read as scenarios, not as JSON plumbing.</summary>
public static class TestData
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static object Order(params (string Sku, int Quantity)[] lines) =>
        new { lines = lines.Select(l => new { sku = l.Sku, quantity = l.Quantity }).ToArray() };

    public static object Order(string? externalReference, params (string Sku, int Quantity)[] lines) =>
        new
        {
            lines = lines.Select(l => new { sku = l.Sku, quantity = l.Quantity }).ToArray(),
            externalReference,
        };

    public static async Task<CreateOrderResponse> ReadOrderAsync(
        this HttpResponseMessage response,
        CancellationToken ct)
    {
        var body = await response.Content.ReadFromJsonAsync<CreateOrderResponse>(Json, ct);
        return body ?? throw new InvalidOperationException("The response body was empty.");
    }

    public static async Task<OrderListResponse> ReadListAsync(
        this HttpResponseMessage response,
        CancellationToken ct)
    {
        var body = await response.Content.ReadFromJsonAsync<OrderListResponse>(Json, ct);
        return body ?? throw new InvalidOperationException("The response body was empty.");
    }
}
