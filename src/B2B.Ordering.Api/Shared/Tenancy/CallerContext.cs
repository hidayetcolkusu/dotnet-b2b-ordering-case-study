using B2B.Ordering.Api.Modules.Access.Contracts;

namespace B2B.Ordering.Api.Shared.Tenancy;

/// <summary>
/// Scoped, write-once holder of the verified caller. Nothing reads a company or user id from the
/// request; everything reads it from here, after the Access module has verified it.
/// </summary>
public sealed class CallerContext
{
    private CompanyAccess? _access;

    public bool IsInitialized => _access is not null;

    public Guid UserId => Current.UserId;

    public Guid CompanyId => Current.CompanyId;

    public string Role => Current.Role;

    private CompanyAccess Current => _access ?? throw new InvalidOperationException(
        "CallerContext was not initialized. Tenant data must not be reached before the caller's "
        + "company membership has been resolved.");

    public void Initialize(CompanyAccess access)
    {
        ArgumentNullException.ThrowIfNull(access);

        if (_access is not null)
        {
            throw new InvalidOperationException(
                "CallerContext is write-once and cannot change inside a request.");
        }

        _access = access;
    }
}
