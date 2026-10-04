using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using B2B.Ordering.Api.Shared.Errors;

namespace B2B.Ordering.Api.Modules.Ordering.Create;

/// <summary>
/// One place normalises and validates the request, and the same normalised value is what gets
/// hashed. That is what makes "same JSON whitespace, different hash" impossible.
/// </summary>
public static class RequestNormalizer
{
    public const int MaxLines = 100;
    public const int MinQuantity = 1;
    public const int MaxQuantity = 1000;
    public const int MaxExternalReferenceLength = 80;
    public const int MaxSkuLength = 40;

    private static readonly JsonSerializerOptions StableJson = new()
    {
        // Fixed, minimal, property-order-stable representation: the hash must depend on the
        // meaning of the request, not on how the client formatted it.
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static NormalizedOrderRequest Normalize(CreateOrderRequest? request)
    {
        if (request is null)
        {
            throw ApiException.Validation("body", "A JSON request body is required.");
        }

        if (request.Lines is null || request.Lines.Count == 0)
        {
            throw ApiException.Validation("lines", "At least one order line is required.");
        }

        if (request.Lines.Count > MaxLines)
        {
            throw ApiException.Validation("lines", $"An order may contain at most {MaxLines} lines.");
        }

        var errors = new Dictionary<string, string[]>();
        var lines = new List<NormalizedOrderLine>(request.Lines.Count);
        var seenSkus = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < request.Lines.Count; index++)
        {
            var line = request.Lines[index];
            var field = $"lines[{index}]";

            // A JSON null inside the array binds to a null element, so it must be checked here.
            if (line is null)
            {
                errors[field] = ["An order line must not be null."];
                continue;
            }

            if (string.IsNullOrWhiteSpace(line.Sku))
            {
                errors[$"{field}.sku"] = ["A SKU is required."];
                continue;
            }

            var sku = line.Sku.Trim().ToUpperInvariant();

            if (sku.Length > MaxSkuLength)
            {
                errors[$"{field}.sku"] = [$"A SKU may be at most {MaxSkuLength} characters."];
                continue;
            }

            if (line.Quantity is < MinQuantity or > MaxQuantity)
            {
                errors[$"{field}.quantity"] =
                    [$"Quantity must be between {MinQuantity} and {MaxQuantity}."];
                continue;
            }

            if (!seenSkus.Add(sku))
            {
                errors[$"{field}.sku"] = ["The same SKU must not appear twice in one order."];
                continue;
            }

            lines.Add(new NormalizedOrderLine(sku, line.Quantity));
        }

        if (errors.Count > 0)
        {
            throw ApiException.Validation("The request body failed validation.", errors);
        }

        var reference = string.IsNullOrWhiteSpace(request.ExternalReference)
            ? null
            : request.ExternalReference.Trim();

        if (reference is { Length: > MaxExternalReferenceLength })
        {
            throw ApiException.Validation(
                "externalReference",
                $"An external reference may be at most {MaxExternalReferenceLength} characters.");
        }

        return new NormalizedOrderRequest(lines, reference);
    }

    /// <summary>
    /// SHA-256 over the normalised request. Line order is part of the meaning, so reordering the
    /// lines is a different payload.
    /// </summary>
    public static string Hash(NormalizedOrderRequest request)
    {
        var json = JsonSerializer.Serialize(request, StableJson);
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(json));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Idempotency keys are visible ASCII, at most 128 characters, compared case sensitively.
    /// </summary>
    public static string NormalizeIdempotencyKey(string? rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey))
        {
            throw new ApiException(
                System.Net.HttpStatusCode.BadRequest,
                ErrorCodes.IdempotencyKeyMissing,
                "Idempotency key required",
                "The Idempotency-Key header is required when creating an order.");
        }

        var key = rawKey.Trim();

        if (key.Length is 0 or > 128 || key.Any(c => c is < ' ' or > '~'))
        {
            throw new ApiException(
                System.Net.HttpStatusCode.BadRequest,
                ErrorCodes.IdempotencyKeyInvalid,
                "Idempotency key invalid",
                "The Idempotency-Key header must be 1 to 128 printable ASCII characters.");
        }

        return key;
    }
}

public sealed record NormalizedOrderRequest(
    IReadOnlyList<NormalizedOrderLine> Lines,
    string? ExternalReference);

public sealed record NormalizedOrderLine(string Sku, int Quantity);
