namespace B2B.Ordering.Tests.Support;

/// <summary>
/// Every test class shares one container. Databases inside it are created per class, so the
/// collection serialises container startup only, not the tests themselves.
/// </summary>
[CollectionDefinition(Name)]
public sealed class SqlServerCollection : ICollectionFixture<SqlServerFixture>
{
    public const string Name = "sqlserver";
}
