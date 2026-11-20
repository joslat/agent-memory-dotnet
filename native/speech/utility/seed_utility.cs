#:project ../../../src/AgentMemory.Neo4j/AgentMemory.Neo4j.csproj
#:project ../../../src/AgentMemory.Core/AgentMemory.Core.csproj
#:package Microsoft.Extensions.DependencyInjection@10.0.12
#:package Microsoft.Extensions.Logging@10.0.12
#:property PublishAot=false

// Seeds the utility run's store through the library's own write path (ILongTermMemoryService), with every vector made
// by a real embedding model on an OpenAI-compatible endpoint, so recall sees what real use would write. The facts are
// fictional. Usage:
//   dotnet run seed_utility.cs -- <bolt-uri> <user> <password-env-var> <embeddings-url> <model> <dimensions>
// The password is read from the named environment variable, never from the command line.
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Neo4j.Infrastructure;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;

if (args.Length != 6)
{
    Console.Error.WriteLine("usage: dotnet run seed_utility.cs -- <bolt-uri> <user> <password-env-var> <embeddings-url> <model> <dimensions>");
    return 2;
}
var password = Environment.GetEnvironmentVariable(args[2]);
if (string.IsNullOrEmpty(password))
{
    Console.Error.WriteLine($"environment variable {args[2]} is empty");
    return 2;
}
var dimensions = int.Parse(args[5]);

var services = new ServiceCollection();
services.AddLogging();
services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new HttpEmbeddings(args[3], args[4], dimensions));
services.AddAgentMemoryCore(o => o.WorkingMemory.Enabled = false);
services.AddNeo4jAgentMemory(o =>
{
    o.Uri = args[0];
    o.Username = args[1];
    o.Password = password;
    o.EmbeddingDimensions = dimensions;
});
await using var provider = services.BuildServiceProvider();
await provider.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync();

await using var scope = provider.CreateAsyncScope();
var memory = scope.ServiceProvider.GetRequiredService<ILongTermMemoryService>();
var now = DateTimeOffset.UtcNow;
(string Owner, string Id, string Predicate, string Object)[] facts =
[
    ("utility-owner-a", "u-a-work", "works_in", "Zurich"),
    ("utility-owner-a", "u-a-dog", "has_a_dog_named", "Mochi"),
    ("utility-owner-a", "u-a-food", "favourite_food_is", "ramen"),
    ("utility-owner-a", "u-a-instrument", "plays_the", "cello"),
    ("utility-owner-a", "u-a-city", "lives_in", "Winterthur"),
    ("utility-owner-b", "u-b-work", "works_in", "Barcelona"),
    ("utility-owner-b", "u-b-dog", "has_a_dog_named", "Rex"),
];
foreach (var (owner, id, predicate, obj) in facts)
{
    var stored = await memory.AddFactAsync(new Fact
    {
        FactId = id, Subject = "user", Predicate = predicate, Object = obj, Confidence = 0.95,
        CreatedAtUtc = now, OwnerId = owner,
    });
    Console.WriteLine($"fact {stored.FactId} owner={owner} vector={stored.Embedding?.Length ?? 0}");
}
(string Owner, string Id, string Text)[] preferences =
[
    ("utility-owner-a", "u-a-concise", "Prefers concise answers"),
    ("utility-owner-b", "u-b-detail", "Prefers detailed answers"),
];
foreach (var (owner, id, text) in preferences)
{
    var stored = await memory.AddPreferenceAsync(new Preference
    {
        PreferenceId = id, Category = "style", PreferenceText = text, Confidence = 0.95, CreatedAtUtc = now,
        OwnerId = owner,
    });
    Console.WriteLine($"preference {stored.PreferenceId} owner={owner} vector={stored.Embedding?.Length ?? 0}");
}
return 0;

/// <summary>An OpenAI-compatible /v1/embeddings client, enough for seeding.</summary>
sealed class HttpEmbeddings(string url, string model, int dimensions) : IEmbeddingGenerator<string, Embedding<float>>
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<GeneratedEmbeddings<Embedding<float>>> GenerateAsync(IEnumerable<string> values,
        EmbeddingGenerationOptions? options = null, CancellationToken cancellationToken = default)
    {
        var input = values.ToArray();
        using var response = await _http.PostAsJsonAsync(url, new { model, input }, cancellationToken);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<EmbeddingsResponse>(cancellationToken)
            ?? throw new InvalidOperationException("empty embeddings response");
        var vectors = body.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToArray();
        if (vectors.Length != input.Length || vectors.Any(v => v.Length != dimensions))
            throw new InvalidOperationException($"expected {input.Length} vectors of {dimensions}");
        return new GeneratedEmbeddings<Embedding<float>>(vectors.Select(v => new Embedding<float>(v)));
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() => _http.Dispose();

    private sealed record EmbeddingsResponse([property: JsonPropertyName("data")] Item[] Data);
    private sealed record Item([property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}
