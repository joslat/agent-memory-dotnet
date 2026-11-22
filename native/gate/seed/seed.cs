#:project ../../../src/AgentMemory.Neo4j/AgentMemory.Neo4j.csproj
#:package Microsoft.Extensions.DependencyInjection@10.0.12
#:package Microsoft.Extensions.Logging@10.0.12
#:property PublishAot=false

// Seeds the gate's fixture store through the managed SDK (ordinary JIT .NET), so the native library reads exactly what
// the library itself writes. Usage: dotnet run seed.cs -- <bolt-uri> <user> <password-env-var-name>
// The password is read from the named environment variable, never from the command line.
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 3)
{
    Console.Error.WriteLine("usage: dotnet run seed.cs -- <bolt-uri> <user> <password-env-var-name>");
    return 2;
}

var password = Environment.GetEnvironmentVariable(args[2]);
if (string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine($"environment variable {args[2]} is empty");
    return 2;
}

var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new NoEmbeddings());
services.AddAgentMemoryCore(_ => { });
services.AddNeo4jAgentMemory(o =>
{
    o.Uri = args[0];
    o.Username = args[1];
    o.Password = password;
    o.EmbeddingDimensions = GateFixture.Dimensions;
});
await using var provider = services.BuildServiceProvider();
await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();

await using var scope = provider.CreateAsyncScope();
var facts = scope.ServiceProvider.GetRequiredService<IFactRepository>();
var preferences = scope.ServiceProvider.GetRequiredService<IPreferenceRepository>();
var now = DateTimeOffset.UtcNow;

foreach (var f in GateFixture.Facts)
{
    var stored = await facts.UpsertAsync(new Fact
    {
        FactId = f.Id,
        Subject = f.Subject,
        Predicate = f.Predicate,
        Object = f.Object,
        Confidence = 0.9,
        CreatedAtUtc = now,
        OwnerId = f.Owner,
        Embedding = GateFixture.Unit(f.Axis),
        Metadata = f.Metadata,
    });
    Console.WriteLine($"fact {stored.FactId} owner={f.Owner} metadata={stored.Metadata.Count}");
}

foreach (var p in GateFixture.Preferences)
{
    var stored = await preferences.UpsertAsync(new Preference
    {
        PreferenceId = p.Id,
        Category = p.Category,
        PreferenceText = p.Text,
        Confidence = 0.9,
        CreatedAtUtc = now,
        OwnerId = p.Owner,
        Embedding = GateFixture.Unit(p.Axis),
        Metadata = p.Metadata,
    });
    Console.WriteLine($"preference {stored.PreferenceId} owner={p.Owner} metadata={stored.Metadata.Count}");
}

return 0;

/// <summary>The fixture: three owners, unit vectors on named axes, empty and nested metadata.</summary>
static class GateFixture
{
    public const int Dimensions = 8;

    public static float[] Unit(int axis)
    {
        var v = new float[Dimensions];
        v[axis] = 1f;
        return v;
    }

    public sealed record FactRow(string Id, string Owner, string Subject, string Predicate, string Object, int Axis,
        IReadOnlyDictionary<string, object> Metadata);

    public sealed record PreferenceRow(string Id, string Owner, string Category, string Text, int Axis,
        IReadOnlyDictionary<string, object> Metadata);

    public static readonly FactRow[] Facts =
    [
        new("gate-fact-a-work", "gate-owner-a", "user", "works_in", "Zurich", 0, new Dictionary<string, object>()),
        new("gate-fact-a-colour", "gate-owner-a", "user", "favourite_colour", "teal", 1, new Dictionary<string, object>
        {
            ["trust_level"] = "verified",
            ["source"] = "gate-seed",
            ["count"] = 3,
            ["ratio"] = 2.5,
            ["flags"] = new object?[] { true, null, "x" },
            ["nested"] = new Dictionary<string, object> { ["depth"] = 2, ["label"] = "Zürich ✓" },
        }),
        new("gate-fact-b-work", "gate-owner-b", "user", "works_in", "Barcelona", 0, new Dictionary<string, object>
        {
            ["trust_level"] = "verified",
        }),
        // A connection to the owner and that person's own fact, on another axis (SPEECH-RELATION-RECALL).
        new("gate-fact-c-sister", "gate-owner-c", "Anna", "sister of", "user", 2, new Dictionary<string, object>()),
        new("gate-fact-c-city", "gate-owner-c", "Anna", "lives in", "Madrid", 5, new Dictionary<string, object>()),
    ];

    public static readonly PreferenceRow[] Preferences =
    [
        new("gate-pref-a-concise", "gate-owner-a", "style", "Prefers concise answers", 2, new Dictionary<string, object>
        {
            ["source"] = "gate-seed",
        }),
        new("gate-pref-b-detail", "gate-owner-b", "style", "Prefers detailed answers", 2, new Dictionary<string, object>()),
    ];
}

/// <summary>Seeding supplies every vector itself.</summary>
sealed class NoEmbeddings : IEmbeddingGenerator<string, Embedding<float>>
{
    public Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException("The seed supplies every vector.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}
