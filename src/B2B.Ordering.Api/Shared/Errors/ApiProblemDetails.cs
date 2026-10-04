using System.Text.Json.Serialization;

namespace B2B.Ordering.Api.Shared.Errors;

/// <summary>
/// Documentation-only shape of the error contract. The pipeline writes
/// <see cref="Microsoft.AspNetCore.Mvc.ProblemDetails"/> with <c>errorCode</c> and <c>traceId</c>
/// in its extensions; this record spells those out so the generated OpenAPI document describes
/// what a client actually receives instead of a bare RFC 9457 envelope.
/// </summary>
public sealed record ApiProblemDetails
{
    /// <summary>URI reference identifying the problem type.</summary>
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    /// <summary>Short, human readable summary of the problem type.</summary>
    [JsonPropertyName("title")]
    public string? Title { get; init; }

    /// <summary>The HTTP status code.</summary>
    [JsonPropertyName("status")]
    public int Status { get; init; }

    /// <summary>Human readable explanation specific to this occurrence.</summary>
    [JsonPropertyName("detail")]
    public string? Detail { get; init; }

    /// <summary>The request path this problem occurred on.</summary>
    [JsonPropertyName("instance")]
    public string? Instance { get; init; }

    /// <summary>
    /// Stable, machine readable code from the closed vocabulary in <see cref="ErrorCodes"/>.
    /// Clients branch on this, never on <see cref="Detail"/>.
    /// </summary>
    [JsonPropertyName("errorCode")]
    public string ErrorCode { get; init; } = string.Empty;

    /// <summary>Correlation id for this request, safe to quote in a support ticket.</summary>
    [JsonPropertyName("traceId")]
    public string? TraceId { get; init; }

    /// <summary>Field level messages, present only for validation failures.</summary>
    [JsonPropertyName("errors")]
    public IReadOnlyDictionary<string, string[]>? Errors { get; init; }
}
