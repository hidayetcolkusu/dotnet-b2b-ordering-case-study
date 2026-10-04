using B2B.Ordering.Api.Modules.Access.Contracts;
using B2B.Ordering.Api.Shared.Auth;
using B2B.Ordering.Api.Shared.Errors;

namespace B2B.Ordering.Api.Shared.Tenancy;

/// <summary>
/// Turns the verified token plus the requested company header into a CallerContext. It lives in
/// Shared because both modules meet here: it consumes the Access contract and produces the tenant
/// context that Ordering reads. It runs as an
/// endpoint filter on the authorised order endpoints only, so it can never shadow a 401 (an
/// unauthenticated request is rejected by authorisation first) and never touches /health.
/// </summary>
public sealed class CompanyContextFilter(
    ICompanyAccessResolver resolver,
    CallerContext callerContext) : IEndpointFilter
{
    public const string CompanyHeader = "X-Company-Id";

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var http = context.HttpContext;

        // Authorisation has already run, so an identity is guaranteed here.
        var userId = http.User.GetUserId();
        var companyId = ReadCompanyId(http);

        var access = await resolver.ResolveAsync(userId, companyId, http.RequestAborted)
            ?? throw ApiException.CompanyAccessDenied();

        callerContext.Initialize(access);

        return await next(context);
    }

    private static Guid ReadCompanyId(HttpContext http)
    {
        if (!http.Request.Headers.TryGetValue(CompanyHeader, out var values) || values.Count == 0)
        {
            throw ApiException.CompanyHeader($"The {CompanyHeader} header is required.");
        }

        if (values.Count > 1)
        {
            throw ApiException.CompanyHeader($"Exactly one {CompanyHeader} header is required.");
        }

        if (!Guid.TryParse(values[0], out var companyId) || companyId == Guid.Empty)
        {
            throw ApiException.CompanyHeader($"The {CompanyHeader} header must be a GUID.");
        }

        return companyId;
    }
}
