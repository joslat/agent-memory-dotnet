using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Schema.Extensions;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Unit.Schema.Extensions;

/// <summary>
/// Every <see cref="ISchemaExtension"/> in the assembly must be reachable from a real container.
/// </summary>
/// <remarks>
/// <para>
/// The <c>ProceduralIncidentTask</c> pattern, applied to schema. That task was written, given seven
/// validity tests, and committed with a message claiming the sample had grown — while the program that
/// runs it still named its task concretely in three places. It was complete, tested, and reachable by
/// nothing. Unit tests could not catch it because they tested the task directly.
/// </para>
/// <para>
/// A schema extension has exactly the same failure mode and a worse blast radius: an extension nobody
/// registered is a "brain update" that no <c>Neo4jOptions.Extensions</c> value can turn on, and its
/// declared shapes never reach the owners report — so the drift it introduces has no owner, which is
/// the very thing this system was built to prevent.
/// </para>
/// </remarks>
public sealed class SchemaExtensionReachabilityTests
{
    private static ServiceProvider BuildContainer() =>
        new ServiceCollection()
            .AddLogging()
            .AddNeo4jAgentMemory(o => o.Uri = "bolt://localhost:7687")
            .BuildServiceProvider();

    private static IReadOnlyList<Type> ImplementationsInAssembly() =>
        [.. typeof(ISchemaExtension).Assembly.GetTypes()
            .Where(type => !type.IsAbstract && !type.IsInterface && type.IsAssignableTo(typeof(ISchemaExtension)))];

    [Fact]
    public void EveryImplementationIsRegisteredInDependencyInjection()
    {
        // THE drift guard, and the direction that actually fails: an extension added to the assembly
        // but left out of AddNeo4jAgentMemory compiles, passes its own tests, and cannot be activated
        // by any configuration a consumer can write.
        var implemented = ImplementationsInAssembly();
        implemented.Should().NotBeEmpty();

        using var provider = BuildContainer();
        var registered = provider.GetServices<ISchemaExtension>().Select(e => e.GetType()).ToHashSet();

        implemented.Should().BeSubsetOf(registered,
            "an ISchemaExtension the container cannot produce is a schema module nobody can enable");
    }

    [Fact]
    public void TheRegistryIsResolvableAndSeesEveryRegisteredExtension()
    {
        using var provider = BuildContainer();

        var registry = provider.GetRequiredService<SchemaExtensionRegistry>();

        registry.All.Should().NotBeEmpty();
        registry.All.Select(e => e.GetType())
            .Should().BeEquivalentTo(provider.GetServices<ISchemaExtension>().Select(e => e.GetType()));
    }

    [Fact]
    public void EveryIdIsUniqueLowercaseKebabAndRoundTripsThroughActivation()
    {
        using var provider = BuildContainer();
        var registry = provider.GetRequiredService<SchemaExtensionRegistry>();

        registry.KnownIds.Should().OnlyHaveUniqueItems();

        foreach (var extension in registry.All)
        {
            SchemaExtensionValidator.ValidateIdentity(extension).Should().BeEmpty(
                $"'{extension.Id}' must satisfy its own identity rules");

            // Round-trip: the id a consumer would copy out of the owners report must be the id that
            // activates it. An extension whose own id does not activate it is unreachable in practice.
            registry.Active([extension.Id]).Should().Contain(extension);
        }
    }

    [Fact]
    public void TheShippedExtensionIdsArePinned()
    {
        // Ids are load-bearing inside (:Migration).version keys, so renaming one orphans every applied
        // ext/<id>/000N row on every database that has them. Pinned here so a rename is a deliberate
        // act with a failing test attached, not a refactor.
        using var provider = BuildContainer();

        provider.GetRequiredService<SchemaExtensionRegistry>().KnownIds
            .Should().Equal("arithmetic", "delta-recall", "procedural", "working-memory");
    }

    [Fact]
    public void EveryDeclaredMigrationScriptShipsNextToTheAssembly()
    {
        // A declared script with no file is schema the database will never receive -- and the symptom
        // would be a missing index found much later, far from the cause.
        using var provider = BuildContainer();

        foreach (var extension in provider.GetRequiredService<SchemaExtensionRegistry>().All)
        {
            foreach (var script in extension.MigrationScripts)
            {
                var path = Path.Combine(
                    AppContext.BaseDirectory, "Schema", "Migrations",
                    MigrationRunner.ExtensionFolder, extension.Id, script);

                File.Exists(path).Should().BeTrue(
                    $"extension '{extension.Id}' declares migration '{script}', which must ship at {path}");
            }
        }
    }

    [Fact]
    public void EveryRetroOwnedBaseMigrationNamesAnExistingBaseScript()
    {
        // The retro-wrap half. procedural claims 0011_trace_kind; if that name drifts, the owners
        // report starts attributing a shape to a migration that no longer exists.
        using var provider = BuildContainer();
        var baseDirectory = Path.Combine(AppContext.BaseDirectory, "Schema", "Migrations");

        foreach (var extension in provider.GetRequiredService<SchemaExtensionRegistry>().All)
        {
            foreach (var version in extension.BaseResidentMigrations)
            {
                File.Exists(Path.Combine(baseDirectory, version + ".cypher")).Should().BeTrue(
                    $"extension '{extension.Id}' retro-owns base migration '{version}', which must exist");
            }
        }
    }

    [Fact]
    public void ADeclaredTckProfileMustPointAtRealCases()
    {
        // TckProfileDescriptor.None is a legitimate state -- the machinery ships ahead of the case
        // convention it defines. What is NOT legitimate is declaring a folder that does not exist,
        // which would report conformance coverage nobody has.
        using var provider = BuildContainer();
        var repositoryRoot = FindRepositoryRoot();

        foreach (var extension in provider.GetRequiredService<SchemaExtensionRegistry>().All)
        {
            if (!extension.TckProfile.IsDeclared) continue;

            var folder = Path.Combine(
                repositoryRoot, "tools", "AgentMemory.TckBridge", extension.TckProfile.CaseFolder);

            Directory.Exists(folder).Should().BeTrue(
                $"extension '{extension.Id}' declares TCK case folder '{folder}'");
            Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories).Length
                .Should().BeGreaterThanOrEqualTo(extension.TckProfile.MinimumCaseCount);
        }
    }

    [Fact]
    public void AnUnknownExtensionIdIsRefusedRatherThanIgnored()
    {
        // Silently ignoring it is how a deployment that asked for an extension runs without one and
        // reports success -- the exact class of defect the whole mechanism exists to make impossible.
        using var provider = BuildContainer();
        var registry = provider.GetRequiredService<SchemaExtensionRegistry>();

        var activate = () => registry.Active(["does-not-exist"]);

        activate.Should().Throw<SchemaInitializationException>()
            .WithMessage("*does-not-exist*").And.Message.Should().Contain("procedural");
    }

    [Fact]
    public void NoExtensionsRequestedActivatesNothing()
    {
        // The byte-identical default, asserted at the activation seam.
        using var provider = BuildContainer();

        provider.GetRequiredService<SchemaExtensionRegistry>()
            .Active(new Neo4jOptions()).Should().BeEmpty();
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AgentMemory.slnx")))
            directory = directory.Parent;

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root from the test output directory.");
    }
}
