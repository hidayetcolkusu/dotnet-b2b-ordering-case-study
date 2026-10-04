using B2B.Ordering.Api.Shared.Persistence;
using Microsoft.Extensions.DependencyInjection;

namespace B2B.Ordering.Tests.Support;

/// <summary>
/// A DbContext plus an open <see cref="MaintenanceScope"/>. Tests use it to assert across
/// companies, which is exactly the documented maintenance escape hatch: query filters are still
/// there, so cross-company reads must be requested explicitly with IgnoreQueryFilters.
/// </summary>
public sealed class MaintenanceContext : IAsyncDisposable
{
    private readonly AsyncServiceScope _scope;
    private readonly MaintenanceScope _maintenance;

    public MaintenanceContext(IServiceProvider services)
    {
        _scope = services.CreateAsyncScope();
        _maintenance = MaintenanceScope.Begin();
        Db = _scope.ServiceProvider.GetRequiredService<AppDbContext>();
    }

    public AppDbContext Db { get; }

    public async ValueTask DisposeAsync()
    {
        _maintenance.Dispose();
        await _scope.DisposeAsync();
    }
}
