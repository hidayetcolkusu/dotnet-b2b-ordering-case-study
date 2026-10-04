namespace B2B.Ordering.Api.Shared.Persistence;

/// <summary>
/// Fixed synthetic identifiers so documentation, scripts and tests can all name the same rows.
/// None of these values come from a real system.
/// </summary>
public static class SeedIds
{
    public static readonly Guid CompanyA = new("11111111-1111-4111-8111-111111111111");
    public static readonly Guid CompanyB = new("22222222-2222-4222-8222-222222222222");
    public static readonly Guid InactiveCompany = new("33333333-3333-4333-8333-333333333333");

    public static readonly Guid CompanyABuyer = new("a1111111-1111-4111-8111-111111111111");
    public static readonly Guid CompanyAViewer = new("a2222222-2222-4222-8222-222222222222");
    public static readonly Guid CompanyBBuyer = new("b1111111-1111-4111-8111-111111111111");

    /// <summary>Active user, member of both A and B as Buyer.</summary>
    public static readonly Guid SharedBuyer = new("c1111111-1111-4111-8111-111111111111");

    /// <summary>Inactive user with an otherwise valid, active membership in company A.</summary>
    public static readonly Guid InactiveUser = new("d1111111-1111-4111-8111-111111111111");

    /// <summary>Active user whose membership row in company A is inactive.</summary>
    public static readonly Guid RevokedMemberUser = new("e1111111-1111-4111-8111-111111111111");

    /// <summary>Active user that is a member of the inactive company only.</summary>
    public static readonly Guid InactiveCompanyUser = new("f1111111-1111-4111-8111-111111111111");

    public static readonly Guid Product1 = new("00000000-0000-4000-8000-000000000001");
    public static readonly Guid Product2 = new("00000000-0000-4000-8000-000000000002");
    public static readonly Guid Product3 = new("00000000-0000-4000-8000-000000000003");
    public static readonly Guid Product4 = new("00000000-0000-4000-8000-000000000004");
    public static readonly Guid InactiveProduct = new("00000000-0000-4000-8000-000000000005");
}
