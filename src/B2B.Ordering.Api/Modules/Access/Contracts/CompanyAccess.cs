namespace B2B.Ordering.Api.Modules.Access.Contracts;

/// <summary>
/// The verified answer to "may this user act for this company, and as what?".
/// Only the Access module produces it; Ordering consumes it through this contract.
/// </summary>
public sealed record CompanyAccess(Guid UserId, Guid CompanyId, string Role);

public static class CompanyRoles
{
    public const string Buyer = "Buyer";
    public const string Viewer = "Viewer";

    public static bool IsKnown(string role) => role is Buyer or Viewer;
}
