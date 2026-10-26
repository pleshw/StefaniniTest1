using System.Reflection;
using NetArchTest.Rules;

namespace Orders.ArchitectureTests;

public sealed class DependencyRuleTests
{
    internal const string Domain = "Orders.Domain";
    internal const string Application = "Orders.Application";
    internal const string Infrastructure = "Orders.Infrastructure";
    internal const string Api = "Orders.Api";
    internal const string Worker = "Orders.Worker";
    internal const string Bff = "Orders.Bff";
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

    private static readonly string[] _hosts = [Api, Worker, Bff];

    public static TheoryData<string> HostNames => new(_hosts);

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
            .ShouldNot().HaveDependencyOnAny([Application, Infrastructure, Api, Worker, Bff, ServiceDefaults, .. _forbiddenInDomain])
            .GetResult();

        AssertNoFailingTypes(result);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Application_ProjectFile_ReferencesNeitherInfrastructureNorHosts()
    {
        ProjectFiles.ProjectReferences(Application).ShouldNotContain(Infrastructure);
        ProjectFiles.ProjectReferences(Application).Intersect(_hosts).ShouldBeEmpty();
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Application_Types_DoNotDependOnInfrastructureOrHosts()
    {
        var result = Types.InAssembly(Load(Application))
            .ShouldNot().HaveDependencyOnAny([Infrastructure, .. _hosts])
            .GetResult();

        AssertNoFailingTypes(result);
    }

    [Theory]
    [MemberData(nameof(HostNames))]
    [Trait("Category", "Architecture")]
    public void Host_ProjectFile_ReferencesNoOtherHost(string host)
    {
        ProjectFiles.ProjectReferences(host).Intersect(_hosts.Except([host])).ShouldBeEmpty();
    }

    [Theory]
    [MemberData(nameof(HostNames))]
    [Trait("Category", "Architecture")]
    public void Host_Types_DoNotDependOnOtherHosts(string host)
    {
        var result = Types.InAssembly(Load(host))
            .ShouldNot().HaveDependencyOnAny([.. _hosts.Except([host])])
            .GetResult();

        AssertNoFailingTypes(result);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Bff_ProjectFile_ReferencesNeitherDomainNorInfrastructure()
    {
        var references = ProjectFiles.ProjectReferences(Bff);

        references.ShouldNotContain(Domain);
        references.ShouldNotContain(Infrastructure);
    }

    [Fact]
    [Trait("Category", "Architecture")]
    public void Bff_Types_DoNotDependOnDomainOrInfrastructure()
    {
        var result = Types.InAssembly(Load(Bff))
            .ShouldNot().HaveDependencyOnAny(Domain, Infrastructure)
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
    public void ServiceDefaults_Types_DoNotDependOnOrdersCode()
    {
        var result = Types.InAssembly(Load(ServiceDefaults))
            .ShouldNot().HaveDependencyOnAny("Orders", "Catalog")
            .GetResult();

        AssertNoFailingTypes(result);
    }

    private static bool StartsWithForbiddenPrefix(string name) =>
        _forbiddenInDomain.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal));

    private static void AssertNoFailingTypes(NetArchTest.Rules.TestResult result) =>
        result.IsSuccessful.ShouldBeTrue($"Violating types: {string.Join(", ", result.FailingTypeNames ?? [])}");

    private static Assembly Load(string name) => Assembly.Load(name);
}
