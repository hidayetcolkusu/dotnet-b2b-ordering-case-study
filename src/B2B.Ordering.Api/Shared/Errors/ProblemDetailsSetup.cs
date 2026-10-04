using Microsoft.AspNetCore.Mvc;

namespace B2B.Ordering.Api.Shared.Errors;

/// <summary>
/// Every supported request leaves through one of three doors: an <see cref="ApiException"/>, an
/// unhandled exception, or a status the pipeline produced without any exception (401 challenge,
/// 403 forbid, 404 unmatched route, 405, 415). All three are wired to one body shape here.
/// </summary>
public static class ProblemDetailsSetup
{
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
                ProblemResponseWriter.Enrich(context.HttpContext, context.ProblemDetails);
        });

        services.AddExceptionHandler<ApiExceptionHandler>();
        return services;
    }

    /// <summary>
    /// Turns exception-free failures into the same body. The built-in
    /// <c>UseStatusCodePages()</c> falls back to a plain-text line when the caller's Accept header
    /// does not include JSON; this handler writes the contract either way.
    /// </summary>
    public static IApplicationBuilder UseApiStatusCodeProblems(this IApplicationBuilder app) =>
        app.UseStatusCodePages(async context =>
        {
            var http = context.HttpContext;

            await ProblemResponseWriter.WriteAsync(
                http,
                new ProblemDetails { Status = http.Response.StatusCode });
        });

    internal static string DefaultErrorCode(int status) => status switch
    {
        400 => ErrorCodes.MalformedRequest,
        401 => ErrorCodes.Unauthenticated,
        403 => ErrorCodes.CompanyAccessDenied,
        404 => ErrorCodes.RouteNotFound,
        405 => ErrorCodes.MethodNotAllowed,
        409 => ErrorCodes.IdempotencyKeyConflict,
        415 => ErrorCodes.UnsupportedMediaType,
        503 => ErrorCodes.IdempotencyBusy,
        _ => ErrorCodes.InternalError,
    };
}
