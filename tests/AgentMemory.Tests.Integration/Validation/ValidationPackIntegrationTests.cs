using System.Text.Json;
using AgentMemory.Tests.Integration.Fixtures;
using AgentMemory.Validation;
using FluentAssertions;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Integration.Validation;

/// <summary>
/// 40.57: every core validation pack passes on a real store, and a planted defect (the pack's feature switched off) fails
/// the check that names it. No model: each pack carries what extraction yields for each message.
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class ValidationPackIntegrationTests : IAsyncLifetime
{
    private readonly Neo4jIntegrationFixture _fixture;
    private readonly ITestOutputHelper _output;

    public ValidationPackIntegrationTests(Neo4jIntegrationFixture fixture, ITestOutputHelper output) =>
        (_fixture, _output) = (fixture, output);

    public Task InitializeAsync() => _fixture.CleanDatabaseAsync();

    public Task DisposeAsync() => Task.CompletedTask;

    public static TheoryData<string> CorePacks() => [.. ValidationPackReader.Core().Select(p => p.Id)];

    [Theory]
    [MemberData(nameof(CorePacks))]
    public async Task Every_core_pack_passes(string id)
    {
        var result = await Runner().RunAsync(ValidationPackReader.Core().Single(p => p.Id == id));
        foreach (var check in result.Checks) _output.WriteLine($"{(check.Passed ? "pass" : "FAIL")} {check.Id}: {check.Detail}");

        result.Failures.Select(f => $"{f.Id}: {f.Detail}").Should().BeEmpty();
        result.Passed.Should().BeTrue();
    }

    /// <summary>
    /// A planted defect: the pack's feature switched off. The pack must fail the checks that name what broke, which is
    /// what makes a passing pack evidence.
    /// </summary>
    [Theory]
    // A change stored as a retraction loses the past, and nothing records why the old value closed.
    [InlineData("core.semantic.changes", "Extraction.BitemporalChanges", "recall:past-city:expect:1,storage:1")]
    // A date in the question is not read: the past question is answered live.
    [InlineData("core.semantic.changes", "ResolveTemporalQueries", "recall:date-in-question:expect:1,recall:date-in-question:exclude:1")]
    // A corrected name adds a second person instead of renaming, and the dog's facts keep the old name.
    [InlineData("core.names", "Extraction.RenameOnCorrectedName", "storage:2,storage:6")]
    // A corrected preference leaves the old one beside it.
    [InlineData("core.preferences", "Extraction.SupersedeReplacedFacts", "recall:morning-drink:exclude:1")]
    public async Task A_feature_switched_off_fails_the_checks_that_name_it(string id, string feature, string failing)
    {
        var pack = Core(id);
        var set = new Dictionary<string, JsonElement>(pack.Options.Set) { [feature] = JsonSerializer.SerializeToElement(false) };

        var result = await Runner().RunAsync(pack with { Options = pack.Options with { Set = set } });

        result.Failures.Select(f => f.Id).Should().Contain(failing.Split(','));
    }

    /// <summary>
    /// 40.56, the router's second check: with routing on, every core pack still passes, so no question loses the memory
    /// that answers it to a kind the router left out.
    /// </summary>
    [Theory]
    [MemberData(nameof(CorePacks))]
    public async Task Every_core_pack_passes_with_routing_on(string id)
    {
        var pack = Core(id);
        var set = new Dictionary<string, JsonElement>(pack.Options.Set) { ["Routing.Enabled"] = JsonSerializer.SerializeToElement(true) };

        var result = await Runner().RunAsync(pack with { Options = pack.Options with { Set = set } });

        result.Failures.Select(f => $"{f.Id}: {f.Detail}").Should().BeEmpty();
    }

    [Fact]
    public async Task With_routing_on_a_statement_recalls_nothing()
    {
        var pack = Core("core.semantic.changes");
        var set = new Dictionary<string, JsonElement>(pack.Options.Set) { ["Routing.Enabled"] = JsonSerializer.SerializeToElement(true) };
        var statement = new PackQuestion
        {
            Id = "a-telling", Owner = "ana", At = DateTimeOffset.Parse("2026-04-10T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            Ask = "I live in Madrid now.", Exclude = [new PackItem { Fact = "Ana | lives in | Madrid" }],
        };

        var result = await Runner().RunAsync(pack with { Options = pack.Options with { Set = set }, Questions = [statement] });

        result.Failures.Should().BeEmpty("a telling is extracted, not answered from memory");
    }

    [Fact]
    public async Task Two_runs_of_one_pack_never_meet()
    {
        var pack = Core("core.semantic.changes");

        var first = await Runner().RunAsync(pack);
        var second = await Runner().RunAsync(pack);

        first.RunPrefix.Should().NotBe(second.RunPrefix);
        second.Failures.Select(f => $"{f.Id}: {f.Detail}").Should().BeEmpty("the second run reads only what it wrote");
    }

    /// <summary>
    /// 41.08 (found by the storage showcase): a caller's embedding generator serves every pack one runner loads. It was
    /// registered so the first pack's store disposed it, and every later pack embedded with a disposed client.
    /// </summary>
    [Fact]
    public async Task A_caller_s_embedding_generator_outlives_each_pack_of_its_runner()
    {
        var pack = Core("core.semantic.changes");
        using var generator = new DisposalWitness(Neo4jIntegrationFixture.TestEmbeddingDimensions);
        var runner = new ValidationPackRunner(o =>
        {
            o.Uri = _fixture.ConnectionString;
            o.Username = _fixture.User;
            o.Password = _fixture.Password;
            o.Database = "neo4j";
            o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
        }, _ => generator);

        await runner.RunAsync(pack);
        var second = await runner.RunAsync(pack);

        generator.Disposed.Should().BeFalse("the generator belongs to the caller, not to a pack");
        generator.CallsAfterDisposal.Should().Be(0);
        second.Failures.Select(f => $"{f.Id}: {f.Detail}").Should().BeEmpty();
    }

    /// <summary>
    /// The runner's own default embedder, watched for disposal. It embeds exactly as a runner without a caller's generator
    /// does: a one-hot vector from <c>string.GetHashCode</c> (randomised per process) over the fixture's four dimensions made
    /// a quarter of unrelated texts identical, so the pack's correction was read as a restatement on some runs (CI, 10-05).
    /// </summary>
    private sealed class DisposalWitness(int dimensions) : Microsoft.Extensions.AI.IEmbeddingGenerator<string, Microsoft.Extensions.AI.Embedding<float>>
    {
        private readonly AgentMemory.Core.Stubs.StubEmbeddingGenerator _inner =
            new(Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentMemory.Core.Stubs.StubEmbeddingGenerator>.Instance, dimensions);

        public bool Disposed { get; private set; }

        public int CallsAfterDisposal { get; private set; }

        public Task<Microsoft.Extensions.AI.GeneratedEmbeddings<Microsoft.Extensions.AI.Embedding<float>>> GenerateAsync(
            IEnumerable<string> values, Microsoft.Extensions.AI.EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
        {
            if (Disposed) CallsAfterDisposal++;
            return _inner.GenerateAsync(values, options, cancellationToken);
        }

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() => Disposed = true;
    }

    private ValidationPackRunner Runner() => new(o =>
    {
        o.Uri = _fixture.ConnectionString;
        o.Username = _fixture.User;
        o.Password = _fixture.Password;
        o.Database = "neo4j";
        o.EmbeddingDimensions = Neo4jIntegrationFixture.TestEmbeddingDimensions;
    });

    private static ValidationPack Core(string id) => ValidationPackReader.Core().Single(p => p.Id == id);
}
