namespace B2B.Ordering.Api.Shared.Persistence;

/// <summary>
/// Raised when application code tries to persist a tenant entity for another company. It is a
/// bug signal, not a business error, so it surfaces as a 500 with no internal detail leaked.
/// </summary>
public sealed class TenantWriteViolationException(
    string entityName,
    Guid attemptedCompanyId,
    Guid callerCompanyId)
    : InvalidOperationException(
        $"Attempted to write {entityName} for company {attemptedCompanyId} "
        + $"while the caller is scoped to company {callerCompanyId}.")
{
    public string EntityName { get; } = entityName;
    public Guid AttemptedCompanyId { get; } = attemptedCompanyId;
    public Guid CallerCompanyId { get; } = callerCompanyId;
}
