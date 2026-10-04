namespace B2B.Ordering.Api.Modules.Access.Data;

public sealed class User
{
    public Guid Id { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
