using ArchUnitNET.Domain;
using ArchUnitNET.Fluent;
using ArchUnitNET.Loader;
using Pragma.Accounts.Domain.Model;
using Pragma.Accounts.Domain.Ports;
using Pragma.Accounts.Domain.UseCases;
using Pragma.Accounts.Infrastructure.Api;
using Pragma.Accounts.Infrastructure.Cache;
using Pragma.Accounts.Infrastructure.CreditScore;
using Pragma.Accounts.Infrastructure.EventStream;
using Pragma.Accounts.Infrastructure.Helpers;
using Pragma.Accounts.Infrastructure.Messaging;
using Pragma.Accounts.Infrastructure.Persistence;
using static ArchUnitNET.Fluent.ArchRuleDefinition;
using Assembly = System.Reflection.Assembly;

namespace Pragma.Accounts.Tests.Architecture;

/// <summary>Reglas de la arquitectura hexagonal (equivalente a ArchUnit en los arquetipos Java).</summary>
public sealed class ArchitectureTests
{
    private static readonly Assembly ModelAssembly = typeof(Account).Assembly;
    private static readonly Assembly PortsAssembly = typeof(IAccountGateway).Assembly;
    private static readonly Assembly UseCasesAssembly = typeof(CreateAccountUseCase).Assembly;

    private static readonly Assembly[] DrivenAdapters =
    [
        typeof(PersistenceServiceCollectionExtensions).Assembly,
        typeof(CacheOptions).Assembly,
        typeof(SqsOptions).Assembly,
        typeof(CreditScoreOptions).Assembly,
        typeof(EventStreamOptions).Assembly,
    ];

    private static readonly Assembly EntryPointAssembly = typeof(AccountEndpoints).Assembly;
    private static readonly Assembly HelpersAssembly = typeof(DataMasker).Assembly;

    private static readonly ArchUnitNET.Domain.Architecture Arch = new ArchLoader()
        .LoadAssemblies([ModelAssembly, PortsAssembly, UseCasesAssembly, EntryPointAssembly, HelpersAssembly, .. DrivenAdapters])
        .Build();

    private static readonly IObjectProvider<IType> Domain =
        Types().That().ResideInAssembly(ModelAssembly, PortsAssembly, UseCasesAssembly).As("Domain");

    private static readonly IObjectProvider<IType> Infrastructure =
        Types().That().ResideInAssembly(EntryPointAssembly, [HelpersAssembly, .. DrivenAdapters]).As("Infrastructure");

    [Fact]
    public void Domain_DoesNotDependOnInfrastructure() =>
        AssertRule(Types().That().Are(Domain).Should().NotDependOnAny(Infrastructure));

    [Fact]
    public void Model_DoesNotDependOnPortsOrUseCases() =>
        AssertRule(Types().That().ResideInAssembly(ModelAssembly).Should()
            .NotDependOnAny(Types().That().ResideInAssembly(PortsAssembly, UseCasesAssembly)));

    [Fact]
    public void EntryPoint_DoesNotDependOnDrivenAdapters() =>
        AssertRule(Types().That().ResideInAssembly(EntryPointAssembly).Should()
            .NotDependOnAny(Types().That().ResideInAssembly(DrivenAdapters[0], DrivenAdapters[1..])));

    [Fact]
    public void DrivenAdapters_DoNotDependOnEachOther()
    {
        foreach (var adapter in DrivenAdapters)
        {
            var others = DrivenAdapters.Where(a => a != adapter).ToArray();
            AssertRule(Types().That().ResideInAssembly(adapter).Should()
                .NotDependOnAny(Types().That().ResideInAssembly(others[0], others[1..])));
        }
    }

    [Fact]
    public void Ports_AreInterfaces() =>
        AssertRule(Types().That().ResideInAssembly(PortsAssembly).Should().Be(Interfaces()));

    [Fact]
    public void UseCases_AreSealed() =>
        AssertRule(Classes().That().ResideInAssembly(UseCasesAssembly).And().HaveNameEndingWith("UseCase").Should().BeSealed());

    /// <summary>El dominio no referencia frameworks: solo la BCL.</summary>
    [Fact]
    public void Domain_ReferencesOnlyBaseClassLibraryAndItself()
    {
        string[] allowed = ["System", "Microsoft.Win32", "netstandard", "Pragma.Accounts.Domain"];
        var offending = new[] { ModelAssembly, PortsAssembly, UseCasesAssembly }
            .SelectMany(a => a.GetReferencedAssemblies().Select(r => $"{a.GetName().Name} -> {r.Name}"))
            .Where(r => !allowed.Any(prefix => r.Split(" -> ")[1].StartsWith(prefix, StringComparison.Ordinal)))
            .ToList();

        Assert.Empty(offending);
    }

    private static void AssertRule(IArchRule rule)
    {
        var violations = rule.Evaluate(Arch).Where(r => !r.Passed).Select(r => r.Description).ToList();
        Assert.True(violations.Count == 0, string.Join(Environment.NewLine, violations));
    }
}
