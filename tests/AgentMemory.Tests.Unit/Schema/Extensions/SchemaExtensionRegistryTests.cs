using AgentMemory.Abstractions.Exceptions;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Schema.Extensions;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Schema.Extensions;

/// <summary>
/// Activation: which extensions a configuration turns on, and in what order their migrations run.
/// </summary>
/// <remarks>
/// Order is the load-bearing part. It decides the sequence extension migration scripts execute in, so
/// an order that varied between processes would be a schema that varied between processes — the kind of
/// nondeterminism that is invisible until two databases disagree.
/// </remarks>
public sealed class SchemaExtensionRegistryTests
{
    private static SchemaExtensionRegistry Registry(params ISchemaExtension[] extensions) => new(extensions);

    private static StubExtension Extension(string id, params string[] dependsOn) => new(id, dependsOn);

    [Fact]
    public void NothingRequestedActivatesNothing()
    {
        // The byte-identical default. With no ids requested this must be indistinguishable from a
        // build where the extension system does not exist.
        Registry(Extension("alpha")).Active(new Neo4jOptions()).Should().BeEmpty();
    }

    [Fact]
    public void AnUnknownIdThrowsAndNamesWhatIsKnown()
    {
        // Throwing rather than ignoring, in SchemaParityPolicy.ForVersion's style: a deployment that
        // asked for an extension and quietly ran without it would report success over the wrong schema.
        var activate = () => Registry(Extension("alpha"), Extension("beta")).Active(["gamma"]);

        activate.Should().Throw<SchemaInitializationException>()
            .WithMessage("*gamma*").And.Message.Should().Contain("alpha").And.Contain("beta");
    }

    [Fact]
    public void ActivationIsOrdinalSortedWhenNothingDependsOnAnythingElse()
    {
        Registry(Extension("charlie"), Extension("alpha"), Extension("bravo"))
            .Active(["charlie", "alpha", "bravo"])
            .Select(e => e.Id).Should().Equal("alpha", "bravo", "charlie");
    }

    [Fact]
    public void RequestOrderDoesNotChangeActivationOrder()
    {
        // Two operators writing the same set in different orders must get the same migration sequence.
        var registry = Registry(Extension("alpha"), Extension("bravo"), Extension("charlie"));

        registry.Active(["charlie", "bravo", "alpha"]).Select(e => e.Id)
            .Should().Equal(registry.Active(["alpha", "bravo", "charlie"]).Select(e => e.Id));
    }

    [Fact]
    public void ADependencyIsActivatedImplicitlyAndRunsFirst()
    {
        // Asking for an extension gets you what it needs. Refusing until the dependency is named would
        // turn a solvable configuration into a startup failure with no safety gained -- the dependency
        // is additive schema either way.
        var registry = Registry(Extension("alpha"), Extension("zulu", "alpha"));

        registry.Active(["zulu"]).Select(e => e.Id).Should().Equal("alpha", "zulu");
    }

    [Fact]
    public void DependencyOrderBeatsAlphabeticalOrder()
    {
        // The tiebreak is ordinal id, but only among extensions that are genuinely independent. If
        // alphabetical ever won over a real dependency, a migration would run before the schema it
        // builds on.
        var registry = Registry(Extension("zulu"), Extension("alpha", "zulu"));

        registry.Active(["alpha"]).Select(e => e.Id).Should().Equal("zulu", "alpha");
    }

    [Fact]
    public void ATransitiveDependencyChainIsFullyOrdered()
    {
        var registry = Registry(Extension("a", "b"), Extension("b", "c"), Extension("c"));

        registry.Active(["a"]).Select(e => e.Id).Should().Equal("c", "b", "a");
    }

    [Fact]
    public void ADependencyCycleIsRefusedRatherThanOrderedArbitrarily()
    {
        // Nothing is applied. An arbitrary order here would produce a database whose schema depends on
        // hash iteration order.
        var activate = () => Registry(Extension("a", "b"), Extension("b", "a")).Active(["a"]);

        activate.Should().Throw<SchemaInitializationException>().WithMessage("*cycle*");
    }

    [Fact]
    public void AMissingDependencyIsRefused()
    {
        var activate = () => Registry(Extension("alpha", "absent")).Active(["alpha"]);

        activate.Should().Throw<SchemaInitializationException>().WithMessage("*absent*not registered*");
    }

    [Fact]
    public void RequestingTheSameIdTwiceActivatesItOnce()
    {
        Registry(Extension("alpha")).Active(["alpha", "alpha"]).Should().ContainSingle();
    }

    [Fact]
    public void RegistrationIsNotActivation()
    {
        // The whole point of the reranker pattern: everything is registered, nothing is on.
        var registry = Registry(Extension("alpha"), Extension("bravo"));

        registry.All.Should().HaveCount(2);
        registry.Active([]).Should().BeEmpty();
        registry.Find("alpha").Should().NotBeNull();
        registry.Find("nope").Should().BeNull();
    }

    [Fact]
    public void TheOptionsOverloadReadsTheExtensionsSet()
    {
        // The seam a consumer actually configures. If this ever stopped reading Neo4jOptions.Extensions
        // the option would be a documented no-op, which is the defect class this system exists to end.
        var options = new Neo4jOptions();
        options.Extensions.Add("alpha");

        Registry(Extension("alpha"), Extension("bravo")).Active(options)
            .Select(e => e.Id).Should().Equal("alpha");
    }

    private sealed class StubExtension(string id, IEnumerable<string> dependsOn) : ISchemaExtension
    {
        public string Id => id;
        public int Version => 1;

        public IReadOnlyDictionary<string, IReadOnlySet<string>> DeclaredProperties { get; } =
            new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

        public IReadOnlySet<string> DeclaredRelationshipTypes { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public IReadOnlySet<string> DeclaredLabels { get; } = new HashSet<string>(StringComparer.Ordinal);
        public IReadOnlyList<string> MigrationScripts { get; } = [];

        public IReadOnlySet<string> BaseResidentMigrations { get; } =
            new HashSet<string>(StringComparer.Ordinal);

        public SchemaParityDelta ParityDelta { get; } = SchemaParityDelta.Empty;
        public IReadOnlySet<string> DependsOn { get; } = new HashSet<string>(dependsOn, StringComparer.Ordinal);
        public TckProfileDescriptor TckProfile { get; } = TckProfileDescriptor.None;
    }
}
