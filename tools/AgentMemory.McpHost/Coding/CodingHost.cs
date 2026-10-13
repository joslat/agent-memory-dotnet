using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using AgentMemory.Extraction.Llm;
using AgentMemory.Inference;
using AgentMemory.Gate;
using Microsoft.Extensions.AI;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

#pragma warning disable AMREC001 // Explicit coding mode selects the recommended preset.
#pragma warning disable AMWRITE001
#pragma warning disable AMGATE001

namespace AgentMemory.McpHost.Coding;

internal static class CodingHost
{
    public static void Configure(WebApplicationBuilder builder, CodingConfiguration configuration)
    {
        // Clear ambient appsettings/environment endpoint configuration before binding the one listener.
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(server =>
        {
            if (configuration.Options.Remote is { } remote)
                server.Listen(IPAddress.Parse(remote.ListenAddress), new Uri(configuration.Options.Url).Port,
                    listener => listener.UseHttps(remote.CertificatePath,
                        configuration.CertificatePassword ?? throw new ArgumentException("Missing configured TLS credential.")));
            else server.Listen(IPAddress.Loopback, new Uri(configuration.Options.Url).Port);
            server.Limits.MaxConcurrentConnections = 64;
            server.Limits.MaxRequestBodySize = CodingBodyLimit.MaximumBytes;
        });
        var host = configuration.Host;
        var services = builder.Services;
        services.AddNeo4jAgentMemory(neo4j =>
        {
            neo4j.Uri = host.Neo4jUri; neo4j.Username = host.Neo4jUsername;
            neo4j.Password = host.Neo4jPassword; neo4j.Database = host.Neo4jDatabase;
            neo4j.EmbeddingDimensions = host.Inference.EmbeddingDimensions!.Value;
        }, store =>
        {
            store.Strategy = MemoryStorageStrategy.DatabasePerApplication;
            store.DatabasePrefix = "coding-"; store.AutoProvision = false;
        });
        var memory = MemoryOptions.CreateRecommended();
        memory.FanOut.Enabled = configuration.Options.AutomaticQuerySplitting;
        // Explicit permission; the job receipt preserves the actual path independently of success.
        memory.Extraction.FallBackToExtractorsWhenWriterFails = configuration.Options.AllowExtractorFallback;
        services.AddAgentMemoryCore(memory);
        services.AddSingleton<IClock, SystemClock>();
        services.AddSingleton<IIdGenerator, GuidIdGenerator>();
        services.AddSingleton<IChatClient>(provider => InferenceClientFactory.TryCreateChatClient(host.Inference,
            "coding writer", out var chat, out _, model: host.Inference.EffectiveExtractionModel)
                ? chat : throw new InvalidOperationException("Cannot construct the coding writer client."));
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(provider =>
            InferenceClientFactory.TryCreateEmbeddingGenerator(host.Inference, out var embeddings, out _)
                ? embeddings : throw new InvalidOperationException("Cannot construct coding embeddings."));
        services.AddLlmExtraction(o => o.ApplyRecommended());
        if (configuration.Options.Judges.Count > 0)
            services.AddAgentMemoryGate(o =>
            {
                o.ApplyRecommended();
                o.Mode = configuration.Options.UseRetrievalGate ? MemoryGateMode.Judge : MemoryGateMode.Floor;
                // Retrieval training/list order does not qualify a model to approve storage closings.
                o.UpdateJudge = configuration.Options.ClosingJudgeName is not null;
                o.UpdateJudgeName = configuration.Options.ClosingJudgeName;
                o.CompoundQuestions = false;
                o.Judges = configuration.Options.Judges.Select(j => new SystemOneEndpoint
                {
                    Name = j.Name, Endpoint = new Uri(j.Endpoint), Model = j.Model,
                    KeyVariable = j.KeyVariable, Weight = j.Weight,
                }).ToList();
            });
        services.AddSingleton(configuration.Options).AddSingleton(configuration.Access).AddSingleton(configuration.Content);
        if (configuration.Options.RecallDiagnostics is { } diagnostics)
            services.AddSingleton(sp => new CodingRecallReceipts(diagnostics, configuration.Content));
        services.AddSingleton<CodingOperationScope>().AddSingleton<CodingMemoryOperations>().AddSingleton<CodingApi>();
        if (configuration.Options.Evidence is { } evidence)
        {
            services.AddSingleton(evidence).AddSingleton<CodingEvidenceReader>().AddSingleton<CodingEvidence>();
        }
        services.AddSingleton<ICodingWriter, CodingWriter>();
        if (configuration.Options.WriteJournal is { } journal)
            services.AddSingleton<ICodingWriteJournal>(_ => new CodingWriteJournal(journal,
                Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new[]
                    { configuration.Options.OwnerId, host.Neo4jUri, "coding-" }))),
                identity => identity.OwnerId == configuration.Options.OwnerId &&
                    configuration.Options.Projects.ContainsKey(identity.ProjectId) &&
                    (configuration.Options.Remote is null || configuration.Options.Remote.AllowWrites)));
        services.AddSingleton(sp => new CodingWriteQueue(sp.GetRequiredService<ICodingWriter>(), configuration.Content,
            retainedJobs: configuration.Options.WriteJournal?.RetainedJobs ?? 512,
            journal: sp.GetService<ICodingWriteJournal>()));
        services.AddHostedService(sp => sp.GetRequiredService<CodingWriteQueue>());
        CodingTransport.AddServices(services);
        if (configuration.Options.Remote is not null) CodingRemoteAuthentication.AddServices(services, configuration.Options);
    }

    public static async Task PrepareAsync(WebApplication app, CodingConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        var scopes = app.Services.GetRequiredService<CodingOperationScope>();
        foreach (var project in configuration.Options.Projects.Keys)
            await scopes.RunAsync(new(configuration.Options.OwnerId, project), async (services, ct) =>
            {
                await services.GetRequiredService<ISchemaBootstrapper>().BootstrapAsync(ct).ConfigureAwait(false);
                return true;
            }, cancellationToken).ConfigureAwait(false);
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || args[0] != "--coding-config" || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine("Coding usage: agent-memory-mcp --coding-config <private.json>");
            return 2;
        }
        CodingConfiguration configuration;
        try { configuration = await CodingConfiguration.ReadAsync(args[1], Environment.GetEnvironmentVariable).ConfigureAwait(false); }
        catch (Exception)
        {
            Console.Error.WriteLine("agent-memory-mcp: invalid or unreadable coding configuration; see docs/coding-service.md.");
            return 2;
        }
        try
        {
            var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], EnvironmentName = "Production" });
            Configure(builder, configuration);
            await using var app = builder.Build();
            using var startup = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            await PrepareAsync(app, configuration, startup.Token).ConfigureAwait(false);
            CodingTransport.Map(app);
            var notice = configuration.Options.Remote is null
                ? "agent-memory-mcp: coding mode, loopback only; input filtering is heuristic; jobs are not durable."
                : "agent-memory-mcp: coding mode, HTTPS/OAuth; input filtering is heuristic; jobs are not durable.";
            Console.Error.WriteLine(configuration.Options.WriteJournal is null ? notice :
                notice.Replace("jobs are not durable.", "bounded durable receipts enabled; uncertain attempts are not replayed.", StringComparison.Ordinal));
            await app.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception)
        {
            Console.Error.WriteLine("agent-memory-mcp: coding startup or service failed; check database readiness and configuration.");
            return 1;
        }
    }
}
