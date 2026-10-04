namespace B2B.Ordering.Api.Modules.Access.Data;

/// <summary>
/// The authorisation record. The role stored here always wins over any role claim in a token.
/// </summary>
public sealed class CompanyMembership
{
    public Guid CompanyId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = string.Empty;
    public bool IsActive { get; set; }

    public Company Company { get; set; } = null!;
    public User User { get; set; } = null!;
}
