namespace B2B.Ordering.Api.Modules.Access.Contracts;

/// <summary>
/// The single seam between Ordering and Access. Ordering never touches Access entities.
/// </summary>
public interface ICompanyAccessResolver
{
    /// <summary>
    /// Returns the verified membership, or <c>null</c> when the user, the company or the
    /// membership is missing or inactive.
    /// </summary>
    Task<CompanyAccess?> ResolveAsync(Guid userId, Guid companyId, CancellationToken ct);
}
