using System.Reflection;
using NetArchTest.Rules;

namespace Catalog.ArchitectureTests;

public sealed class DependencyRuleTests
{
    internal const string Domain = "Catalog.Domain";
    internal const string Application = "Catalog.Application";
    internal const string Infrastructure = "Catalog.Infrastructure";
    internal const string Api = "Catalog.Api";
    internal const string ServiceDefaults = "ServiceDefaults";

    private static readonly string[] _forbiddenInDomain =
    [
        "Microsoft.EntityFrameworkCore",
        "Npgsql",
        "MongoDB",
        "Microsoft.AspNetCore",
        "Wolverine",
        "RabbitMQ",
    ];

    [Fact]
    [Trait("Category", "Architecture")]
    public void Domain_ProjectFile_ReferencesNoOtherProject()
    {
        ProjectFiles.ProjectReferences(Domain).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Domain_ProjectFile_ReferencesNoPersistenceWebOrMessagingPackage()
    {
        var references = ProjectFiles.PackageReferences(Domain).Concat(ProjectFiles.FrameworkReferences(Domain));

        references.Where(StartsWithForbiddenPrefix).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Domain_Types_DoNotDependOnOtherProjectsOrFrameworks()
    {
        var result = Types.InAssembly(Load(Domain))
            .ShouldNot().HaveDependencyOnAny([Application, Infrastructure, Api, ServiceDefaults, .. _forbiddenInDomain])
            .GetResult();

        AssertNoFailingTypes(result);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Application_ProjectFile_ReferencesNeitherInfrastructureNorHost()
    {
        var references = ProjectFiles.ProjectReferences(Application);

        references.ShouldNotContain(Infrastructure);
        references.ShouldNotContain(Api);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Application_Types_DoNotDependOnInfrastructureOrHost()
    {
        var result = Types.InAssembly(Load(Application))
            .ShouldNot().HaveDependencyOnAny(Infrastructure, Api)
            .GetResult();

        AssertNoFailingTypes(result);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void ServiceDefaults_ProjectFile_ReferencesNoServiceProject()
    {
        ProjectFiles.ProjectReferences(ServiceDefaults).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void ServiceDefaults_Types_DoNotDependOnCatalogCode()
    {
        var result = Types.InAssembly(Load(ServiceDefaults))
            .ShouldNot().HaveDependencyOnAny("Catalog", "Orders")
            .GetResult();

        AssertNoFailingTypes(result);
    }

    private static bool StartsWithForbiddenPrefix(string name) =>
        _forbiddenInDomain.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private static void AssertNoFailingTypes(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful.ShouldBeTrue($"Violating types: {string.Join(", ", result.FailingTypeNames ?? [])}");

    private static Assembly Load(string name) => Assembly.Load(name);
}
