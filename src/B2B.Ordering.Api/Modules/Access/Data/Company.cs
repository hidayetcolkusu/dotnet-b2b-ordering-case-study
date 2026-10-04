namespace B2B.Ordering.Api.Modules.Access.Data;

public sealed class Company
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; }
}
