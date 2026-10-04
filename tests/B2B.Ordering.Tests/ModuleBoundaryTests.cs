using System.Reflection;
using B2B.Ordering.Api.Shared.Persistence;
using NetArchTest.Rules;

namespace B2B.Ordering.Tests;

/// <summary>
/// The claim "these are two modules" is only worth making if something checks it. NetArchTest is
/// used because it reads IL, so a forbidden type used inside a method body is caught too - a
/// reflection-only check over public members would miss exactly the cases that matter.
/// </summary>
public sealed class ModuleBoundaryTests
{
    private const string AccessNamespace = "B2B.Ordering.Api.Modules.Access";
    private const string AccessContracts = "B2B.Ordering.Api.Modules.Access.Contracts";
    private const string OrderingNamespace = "B2B.Ordering.Api.Modules.Ordering";

    private static readonly Assembly Api = typeof(AppDbContext).Assembly;

    /// <summary>
    /// The forbidden list is derived from the assembly rather than hard-coded, so a new Access
    /// type cannot silently escape the rule. Full type names are used instead of the Access
    /// namespace because NetArchTest matches dependencies by prefix, and the Access namespace is
    /// a prefix of Access.Contracts - the one part Ordering is allowed to use.
    /// </summary>
    private static string[] AccessInternals() => Api.GetTypes()
        .Where(type => type.Namespace is { } ns
            && (ns == AccessNamespace || ns.StartsWith(AccessNamespace + ".", StringComparison.Ordinal))
            && ns != AccessContracts
            && !ns.StartsWith(AccessContracts + ".", StringComparison.Ordinal))
        .Select(type => type.FullName)
        .Where(name => name is not null)
        .Select(name => name!)
        .Distinct()
        .ToArray();

    [Fact]
    public void OrderingDependsOnAccessOnlyThroughContracts()
    {
        var forbidden = AccessInternals();
        Assert.NotEmpty(forbidden);

        var result = Types.InAssembly(Api)
            .That().ResideInNamespaceStartingWith(OrderingNamespace)
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result, "Ordering reached into Access internals"));
    }

    [Fact]
    public void AccessDoesNotDependOnOrdering()
    {
        var result = Types.InAssembly(Api)
            .That().ResideInNamespaceStartingWith(AccessNamespace)
            .ShouldNot().HaveDependencyOn(OrderingNamespace)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result, "Access reached into Ordering"));
    }

    [Fact]
    public void SharedInfrastructureIsTheOnlyPlaceThatSeesBothModules()
    {
        // The documented exception (ADR 0001): AppDbContext merges both modules' mappings, and the
        // company context filter turns an Access contract into the tenant context Ordering reads.
        // Nothing else in Shared may depend on Access internals.
        var forbidden = AccessInternals();

        var result = Types.InAssembly(Api)
            .That().ResideInNamespaceStartingWith("B2B.Ordering.Api.Shared")
            .And().DoNotHaveName(nameof(AppDbContext))
            .And().DoNotHaveName(nameof(SeedData))
            .And().DoNotResideInNamespace("B2B.Ordering.Api.Shared.Persistence.Configurations")
            .ShouldNot().HaveDependencyOnAny(forbidden)
            .GetResult();

        Assert.True(result.IsSuccessful, Describe(result, "Shared code outside the documented exception reached into Access internals"));
    }

    private static string Describe(NetArchTest.Rules.TestResult result, string message)
    {
        var offenders = result.FailingTypeNames is null
            ? "(none reported)"
            : string.Join(", ", result.FailingTypeNames);

        return $"{message}: {offenders}";
    }
}
