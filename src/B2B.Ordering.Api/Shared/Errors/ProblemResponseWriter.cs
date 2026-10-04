using System.Diagnostics;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;

namespace B2B.Ordering.Api.Shared.Errors;

/// <summary>
/// Writes the error contract, whatever the caller asked for in <c>Accept</c>.
///
/// <see cref="IProblemDetailsService"/> performs content negotiation and simply declines to write
/// when the request accepts neither JSON nor <c>*&#47;*</c>. Declining is defensible for a success
/// body, but for an error it leaves an integrator holding a bare status code with no reason and no
/// trace id. So the service is still given first refusal — it applies the shared customization —
/// and this writer takes over when it declines.
/// </summary>
internal static class ProblemResponseWriter
{
    public const string ContentType = "application/problem+json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Fills in the fields every error body carries. Also used by the ProblemDetails service
    /// customization, so both paths produce the same document.
    /// </summary>
    public static void Enrich(HttpContext http, ProblemDetails problem)
    {
        problem.Status ??= http.Response.StatusCode;
        problem.Type ??= TypeFor(problem.Status.Value);
        problem.Title ??= TitleFor(problem.Status.Value);
        problem.Detail ??= DetailFor(problem.Status.Value);
        problem.Instance ??= http.Request.Path.Value;
        problem.Extensions["traceId"] = Activity.Current?.Id ?? http.TraceIdentifier;

        if (!problem.Extensions.ContainsKey("errorCode"))
        {
            problem.Extensions["errorCode"] = ProblemDetailsSetup.DefaultErrorCode(problem.Status.Value);
        }
    }

    /// <summary>
    /// Offers the body to the ProblemDetails service and, if it declines, writes it directly.
    /// Returns false only when the response has already started.
    /// </summary>
    public static async Task<bool> WriteAsync(
        HttpContext http,
        ProblemDetails problem,
        Exception? exception = null)
    {
        if (http.Response.HasStarted)
        {
            return false;
        }

        http.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        var service = http.RequestServices.GetService<IProblemDetailsService>();

        if (service is not null)
        {
            var written = await service.TryWriteAsync(new ProblemDetailsContext
            {
                HttpContext = http,
                Exception = exception,
                ProblemDetails = problem,
            });

            if (written)
            {
                return true;
            }
        }

        // The caller accepts nothing we negotiate for. It still gets the contract.
        Enrich(http, problem);
        http.Response.ContentType = ContentType;
        await http.Response.WriteAsync(JsonSerializer.Serialize(problem, Json));

        return true;
    }

    private static string TypeFor(int status) => status switch
    {
        400 => "https://tools.ietf.org/html/rfc9110#section-15.5.1",
        401 => "https://tools.ietf.org/html/rfc9110#section-15.5.2",
        403 => "https://tools.ietf.org/html/rfc9110#section-15.5.4",
        404 => "https://tools.ietf.org/html/rfc9110#section-15.5.5",
        405 => "https://tools.ietf.org/html/rfc9110#section-15.5.6",
        409 => "https://tools.ietf.org/html/rfc9110#section-15.5.10",
        415 => "https://tools.ietf.org/html/rfc9110#section-15.5.16",
        503 => "https://tools.ietf.org/html/rfc9110#section-15.6.4",
        _ => "https://tools.ietf.org/html/rfc9110#section-15.6.1",
    };

    private static string TitleFor(int status) => status switch
    {
        400 => "Malformed request",
        401 => "Unauthorized",
        403 => "Forbidden",
        404 => "Not found",
        405 => "Method not allowed",
        409 => "Conflict",
        415 => "Unsupported media type",
        503 => "Service unavailable",
        _ => "Request failed",
    };

    private static string DetailFor(int status) => status switch
    {
        400 => "The request could not be read as a valid request for this endpoint.",
        401 => "A valid bearer token is required.",
        403 => "The authenticated user may not perform this operation.",
        404 => "No endpoint or resource matched this request.",
        405 => "This HTTP method is not supported for this route.",
        415 => "The request content type is not supported. Use application/json.",
        503 => "The request could not be completed right now. Retry shortly.",
        _ => "The request could not be completed.",
    };
}
