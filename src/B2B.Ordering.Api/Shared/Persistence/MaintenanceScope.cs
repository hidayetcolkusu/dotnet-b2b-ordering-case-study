namespace B2B.Ordering.Api.Shared.Persistence;

/// <summary>
/// Opt-in maintenance mode for seeding and migrations. It is deliberately not reachable from an
/// HTTP request: no header, query string or CallerContext state can switch it on. Only code that
/// runs outside the request pipeline can open a scope.
/// </summary>
public sealed class MaintenanceScope : IDisposable
{
    private static readonly AsyncLocal<bool> ActiveFlag = new();

    private readonly bool _previous;
    private bool _disposed;

    private MaintenanceScope()
    {
        _previous = ActiveFlag.Value;
        ActiveFlag.Value = true;
    }

    public static bool IsActive => ActiveFlag.Value;

    public static MaintenanceScope Begin() => new();

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ActiveFlag.Value = _previous;
        _disposed = true;
    }
}
