using System.Text.Json;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace B2B.Ordering.Api.Shared.Errors;

/// <summary>
/// Maps exceptions to the shared ProblemDetails contract. Unexpected exceptions are logged in
/// full and reported as a bare 500: no stack trace or SQL text ever reaches the client.
/// </summary>
public sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        var problem = exception switch
        {
            ApiException api => FromApiException(api),
            BadHttpRequestException bad => FromBadRequest(bad),
            JsonException => Malformed(),
            _ => Unexpected(exception, httpContext),
        };

        httpContext.Response.StatusCode = problem.Status ?? StatusCodes.Status500InternalServerError;

        if (exception is ApiException { ErrorCode: ErrorCodes.IdempotencyBusy })
        {
            httpContext.Response.Headers.RetryAfter = "1";
        }

        // The writer offers the body to the ProblemDetails service first and writes it directly if
        // content negotiation declines, so an unusual Accept header cannot cost the caller the
        // reason for the failure.
        return await ProblemResponseWriter.WriteAsync(httpContext, problem, exception);
    }

    private static ProblemDetails FromApiException(ApiException exception)
    {
        var problem = new ProblemDetails
        {
            Status = (int)exception.Status,
            Title = exception.Title,
            Detail = exception.Detail,
        };

        problem.Extensions["errorCode"] = exception.ErrorCode;

        if (exception.Errors is { Count: > 0 })
        {
            problem.Extensions["errors"] = exception.Errors;
        }

        return problem;
    }

    private static ProblemDetails FromBadRequest(BadHttpRequestException exception)
    {
        // Minimal API binding failures (malformed JSON, unknown member, wrong media type) arrive
        // here as BadHttpRequestException. They are caller mistakes, but the framework message can
        // quote payload fragments, so only a fixed description is returned.
        var isMediaType = exception.StatusCode == StatusCodes.Status415UnsupportedMediaType;

        var problem = new ProblemDetails
        {
            Status = isMediaType
                ? StatusCodes.Status415UnsupportedMediaType
                : StatusCodes.Status400BadRequest,
            Title = isMediaType ? "Unsupported media type" : "Malformed request",
            Detail = isMediaType
                ? "The request content type is not supported. Use application/json."
                : "The request body could not be read as the expected JSON document.",
        };

        problem.Extensions["errorCode"] = isMediaType
            ? ErrorCodes.UnsupportedMediaType
            : ErrorCodes.MalformedRequest;

        return problem;
    }

    private static ProblemDetails Malformed()
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Malformed request",
            Detail = "The request body could not be read as the expected JSON document.",
        };

        problem.Extensions["errorCode"] = ErrorCodes.MalformedRequest;
        return problem;
    }

    private ProblemDetails Unexpected(Exception exception, HttpContext httpContext)
    {
        logger.LogError(
            exception,
            "Unhandled exception for {Method} {Path}",
            httpContext.Request.Method,
            httpContext.Request.Path);

        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status500InternalServerError,
            Title = "Unexpected error",
            Detail = "The request could not be completed because of an unexpected error.",
        };

        problem.Extensions["errorCode"] = ErrorCodes.InternalError;
        return problem;
    }
}
