using System.Reflection;
using FluentValidation;

namespace Orders.ArchitectureTests;

/// <summary>Commands and queries are public records, and each one has a FluentValidation validator.</summary>
public sealed class MessageConventionTests
{
    private static readonly Type[] _messages = Assembly.Load(DependencyRuleTests.Application).GetTypes()
        .Where(type => type.Name.EndsWith("Command", StringComparison.Ordinal) || type.Name.EndsWith("Query", StringComparison.Ordinal))
        .ToArray();

    [Fact]
    [Trait("Category", "Architecture")]
    public void Application_DeclaresCommandsAndQueries()
    {
        _messages.ShouldNotBeEmpty();
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void CommandsAndQueries_ArePublicSealedRecords()
    {
        var violations = _messages.Where(type => !type.IsPublic || !type.IsSealed || !IsRecord(type)).Select(type => type.FullName);

        violations.ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void CommandsAndQueries_EachHaveAValidator()
    {
        var validated = Assembly.Load(DependencyRuleTests.Application).GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .SelectMany(type => type.GetInterfaces())
            .Where(contract => contract.IsGenericType && contract.GetGenericTypeDefinition() == typeof(IValidator<>))
            .Select(contract => contract.GetGenericArguments()[0])
            .ToHashSet();

        _messages.Where(type => !validated.Contains(type)).Select(type => type.FullName).ShouldBeEmpty();
    }

    /// <summary>The compiler gives every record a hidden <c>&lt;Clone&gt;$</c> method.</summary>
    private static bool IsRecord(Type type) => type.GetMethod("<Clone>$") is not null;
}
