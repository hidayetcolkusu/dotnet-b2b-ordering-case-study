using B2B.Ordering.Api.Modules.Access.Contracts;
using B2B.Ordering.Api.Shared.Persistence;
using Microsoft.EntityFrameworkCore;

namespace B2B.Ordering.Api.Modules.Access;

/// <summary>
/// Resolves membership from the Access tables. These tables carry no tenant query filter on
/// purpose: this lookup is what establishes the tenant context in the first place.
/// </summary>
public sealed class CompanyAccessResolver(AppDbContext db) : ICompanyAccessResolver
{
    public async Task<CompanyAccess?> ResolveAsync(Guid userId, Guid companyId, CancellationToken ct)
    {
        var row = await db.CompanyMemberships
            .AsNoTracking()
            .Where(m => m.UserId == userId
                && m.CompanyId == companyId
                && m.IsActive
                && m.User.IsActive
                && m.Company.IsActive)
            .Select(m => new { m.Role })
            .SingleOrDefaultAsync(ct);

        if (row is null || !CompanyRoles.IsKnown(row.Role))
        {
            return null;
        }

        // The role comes from the database, never from a claim in the token.
        return new CompanyAccess(userId, companyId, row.Role);
    }
}
