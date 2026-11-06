extern alias McpHost;

using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Stubs;
using AgentMemory.Extensibility.Bundles;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using McpHost::AgentMemory.McpHost.Coding;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;

#pragma warning disable AMWRITE001 // Deterministic writers, no model endpoint, over the real persistence pipeline.
#pragma warning disable AMEXT001

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class StoreIsolationIntegrationTests
{
    /// <summary>
    /// Extensibility 0.13 E9 and E10 in coding mode, on real per-project databases: a remembered turn writes core and a
    /// repository lesson bound to the turn's commit, a fresh session's recall returns the lesson in its module section, a signed
    /// knowledge pack is searched in every project, and another project gets none of the lessons.
    /// </summary>
    [Fact]
    public async Task Coding_modules_write_lessons_bound_to_the_commit_and_recall_them_only_in_their_project()
    {
        var neo4j = new Neo4jOptions
        {
            Uri = _container!.GetConnectionString(), Username = Username, Password = Password, Database = "neo4j", EmbeddingDimensions = Dimensions,
        };
        var provision = new Neo4jMemoryStoreProvisioner(_sessionFactory, Options.Create(neo4j), Options.Create(new MemoryStoreOptions
        {
            Strategy = MemoryStorageStrategy.DatabasePerApplication, DatabasePrefix = "coding-", AutoProvision = true,
        }), NullLogger<Neo4jMemoryStoreProvisioner>.Instance);
        await provision.EnsureStoreAsync("gamma");
        await provision.EnsureStoreAsync("delta");

        var folder = Directory.CreateTempSubdirectory("coding-modules-").FullName;
        try
        {
            var key = BundleSigning.CreateKey();
            var publicKey = Path.Combine(folder, "dev.public.pem");
            File.WriteAllText(publicKey, key.PublicKeyPem);
            var bundle = Path.Combine(folder, "guide");
            Directory.CreateDirectory(Path.Combine(bundle, "seed"));
            foreach (var (path, bytes) in BundleSigning.Sign(new Dictionary<string, byte[]>
            {
                ["manifest.json"] = Encoding.UTF8.GetBytes("""{"format":"agentmemory.module/v1alpha1","id":"io.example.codeguide","version":"1.0.0","sdk":"[0.1,0.2)","seed":["seed/records.jsonl"],"ontology":{"labelPrefix":"CodeGuide","types":[{"name":"CodeGuideTip","scope":"shared","render":"{text}","properties":{"text":{"type":"string","required":true}}}]},"views":[{"id":"tips","type":"CodeGuideTip","search":{"properties":["text"]},"limit":4}]}"""),
                ["seed/records.jsonl"] = Encoding.UTF8.GetBytes("""{"type":"CodeGuideTip","key":"t1","properties":{"text":"Run dotnet test with -m:2 to keep memory down"}}""" + "\n"),
            }, "io.example.codeguide", "1.0.0", key.PrivateKeyPem))
                File.WriteAllBytes(Path.Combine(bundle, path.Replace('/', Path.DirectorySeparatorChar)), bytes);

            using var reservation = new TcpListener(IPAddress.Loopback, 0);
            reservation.Start();
            var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
            reservation.Stop();
            var environment = new Dictionary<string, string>
            {
                ["AI_INFERENCE_PROVIDER"] = "ollama", ["OLLAMA_MODEL"] = "fixture",
                ["AI_EMBEDDING_DIMENSIONS"] = Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ["NEO4J_URI"] = neo4j.Uri, ["NEO4J_USERNAME"] = Username, ["NEO4J_PASSWORD"] = Password,
                ["GAMMA_TOKEN"] = new string('g', 40), ["DELTA_TOKEN"] = new string('d', 40),
            };
            var config = CodingConfiguration.Parse(System.Text.Json.JsonSerializer.Serialize(new
            {
                ownerId = "owner", projects = new { gamma = "GAMMA_TOKEN", delta = "DELTA_TOKEN" }, url = $"http://127.0.0.1:{port}",
                modules = new { repository = true, packs = new[] { bundle }, developerKeys = new[] { publicKey } },
            }), environment.GetValueOrDefault);
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
            CodingHost.Configure(builder, config);
            var services = builder.Services;
            // The one model in this host is the repository writer's: the core writer is a deterministic stand-in.
            var lessons = new ScriptedRepositoryModel();
            services.RemoveAll<IChatClient>();
            services.AddSingleton<IChatClient>(lessons);
            services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
            services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new StubEmbeddingGenerator(NullLogger<StubEmbeddingGenerator>.Instance, Dimensions));
            // The stub's vectors carry no meaning, so this host finds packs by their words (feature 19: meaning search is on by
            // default with an embedder, and random neighbours would take the places of the word hits this test is about).
#pragma warning disable AMEXT001
            services.RemoveAll<AgentMemory.Extensibility.Neo4j.Storage.PackMeaningDefault>();
            services.AddSingleton(new AgentMemory.Extensibility.Neo4j.Storage.PackMeaningDefault(null));
#pragma warning restore AMEXT001
            services.RemoveAll<IMemoryWriter>();
            services.AddSingleton<IMemoryWriter>(new CodingTestWriter());
            var extractor = Substitute.For<IUnifiedMemoryExtractor>();
            services.RemoveAll<IUnifiedMemoryExtractor>();
            services.AddSingleton(extractor);
            await using var app = builder.Build();
            await CodingHost.PrepareAsync(app, config);
            CodingTransport.Map(app);
            var scopes = app.Services.GetRequiredService<CodingOperationScope>();
            var gamma = new CodingIdentity("owner", "gamma");
            var delta = new CodingIdentity("owner", "delta");

            await app.StartAsync();
            using var client = new HttpClient { BaseAddress = new Uri(config.Options.Url) };
            try
            {
                var turn = new CodingRememberRequest
                {
                    Session = "build-session", TurnId = "t1", Commit = new string('c', 40), Source = "claude-code",
                    UserText = "The integration build failed with MSB3021 Unable to copy file because the arena process held the DLLs; "
                        + "building with --artifacts-path artifacts/alt-build fixed it and the build passed.",
                };
                using (var remember = new HttpRequestMessage(HttpMethod.Post, "/projects/gamma/remember") { Content = JsonContent.Create(turn, options: CodingApi.Json) })
                {
                    remember.Headers.Authorization = new("Bearer", environment["GAMMA_TOKEN"]);
                    using var accepted = await client.SendAsync(remember);
                    accepted.StatusCode.Should().Be(HttpStatusCode.Accepted);
                    var job = (await accepted.Content.ReadFromJsonAsync<CodingWriteJob>(CodingApi.Json))!;
                    (await AwaitCodingJobAsync(client, "gamma", environment["GAMMA_TOKEN"], job.JobId)).State.Should().Be(CodingJobState.Succeeded);
                }

                // E9: the lesson is stored in gamma's database, bound to the turn's commit and project, never the model's.
                var stored = await scopes.RunAsync(gamma, (sp, ct) => sp.GetRequiredService<INeo4jTransactionRunner>().ReadAsync(async tx =>
                    await (await tx.RunAsync("MATCH (n:RepoLesson) WHERE n.invalidated_at IS NULL RETURN n.key AS key, n.commit AS commit, n.repository AS repository, n.kind AS kind")).ToListAsync(), ct));
                stored.Should().ContainSingle();
                stored[0]["commit"].ToString().Should().Be(new string('c', 40));
                stored[0]["repository"].ToString().Should().Be("gamma");
                stored[0]["kind"].ToString().Should().Be("failure-fix");
                lessons.Calls.Should().Be(1);

                var lessonRecall = await RecallAsync("gamma", "fresh", "How do I get past MSB3021 when DLLs are locked?");
                lessonRecall.GetProperty("modules").EnumerateArray().Should().Contain(m =>
                    m.GetProperty("module").GetString() == "io.agentmemory.packs.repository" && m.GetProperty("text").GetString()!.Contains("alt-build"));
                var tipRecall = await RecallAsync("gamma", "fresh-2", "Which dotnet test settings keep memory down?");
                tipRecall.GetProperty("modules").EnumerateArray().Should().Contain(m =>
                    m.GetProperty("module").GetString() == "io.example.codeguide" && m.GetProperty("pack").GetString() == "io.example.codeguide@1.0.0");

                // E10: delta has the shared pack, imported into its own database, and none of gamma's lessons.
                var deltaRecall = await RecallAsync("delta", "fresh", "How do I get past MSB3021 when DLLs are locked?");
                deltaRecall.GetProperty("modules").EnumerateArray().Should().NotContain(m => m.GetProperty("module").GetString() == "io.agentmemory.packs.repository");
                (await RecallAsync("delta", "fresh-2", "Which dotnet test settings keep memory down?")).GetProperty("modules").EnumerateArray()
                    .Should().Contain(m => m.GetProperty("module").GetString() == "io.example.codeguide");
                (await scopes.RunAsync(delta, (sp, ct) => sp.GetRequiredService<INeo4jTransactionRunner>().ReadAsync(async tx =>
                    await (await tx.RunAsync("MATCH (n:RepoLesson) RETURN count(n) AS n")).SingleAsync(), ct)))["n"].ToString().Should().Be("0");
            }
            finally { await app.StopAsync(); }

            async Task<System.Text.Json.JsonElement> RecallAsync(string project, string session, string query)
            {
                using var message = new HttpRequestMessage(HttpMethod.Post, $"/projects/{project}/recall") { Content = JsonContent.Create(new { session, query }) };
                message.Headers.Authorization = new("Bearer", environment[project.ToUpperInvariant() + "_TOKEN"]);
                using var response = await client.SendAsync(message);
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadFromJsonAsync<System.Text.Json.JsonElement>();
            }
        }
        finally { Directory.Delete(folder, recursive: true); }
    }

    /// <summary>The repository writer's model: one failure-fix lesson for a build turn, nothing otherwise.</summary>
    private sealed class ScriptedRepositoryModel : IChatClient
    {
        public int Calls { get; private set; }
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var reply = messages.Last().Text.Contains("MSB3021", StringComparison.Ordinal)
                ? """{"lessons":[{"key":"msb3021-dll-lock","kind":"failure-fix","lesson":"MSB3021 means a process holds the DLLs; build with --artifacts-path artifacts/alt-build.","failure":"MSB3021 Unable to copy file","fix":"dotnet build --artifacts-path artifacts/alt-build","commit":"made-up-by-the-model"}]}"""
                : """{"lessons":[]}""";
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)) { FinishReason = ChatFinishReason.Stop });
        }
        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public object? GetService(Type serviceType, object? serviceKey = null) => null;
        public void Dispose() { }
    }
}
