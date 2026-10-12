extern alias McpHost;

using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using McpHost::AgentMemory.McpHost.Coding;
using AgentMemory.Neo4j.Infrastructure;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection.Extensions;
using System.Net;
using System.Net.Sockets;
using System.Net.Http.Json;
using NSubstitute;

#pragma warning disable AMWRITE001 // Deterministic writer, no model endpoint, over the real persistence pipeline.

namespace AgentMemory.Tests.Integration.Repositories;

public sealed partial class StoreIsolationIntegrationTests
{
    [Fact]
    public async Task Coding_worker_persists_sources_and_facts_only_in_the_bound_project_and_forgets_non_destructively()
    {
        var neo4j = new Neo4jOptions
        {
            Uri = _container!.GetConnectionString(), Username = Username, Password = Password,
            Database = "neo4j", EmbeddingDimensions = Dimensions,
        };
        // Test setup alone provisions the two databases. The service itself cannot create one.
        var provision = new Neo4jMemoryStoreProvisioner(_sessionFactory, Options.Create(neo4j),
            Options.Create(new MemoryStoreOptions
            {
                Strategy = MemoryStorageStrategy.DatabasePerApplication, DatabasePrefix = "coding-", AutoProvision = true,
            }), NullLogger<Neo4jMemoryStoreProvisioner>.Instance);
        await provision.EnsureStoreAsync("alpha");
        await provision.EnsureStoreAsync("beta");

        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        var environment = new Dictionary<string, string>
        {
            ["AI_INFERENCE_PROVIDER"] = "ollama", ["OLLAMA_MODEL"] = "fixture",
            ["AI_EMBEDDING_DIMENSIONS"] = Dimensions.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["NEO4J_URI"] = neo4j.Uri, ["NEO4J_USERNAME"] = Username, ["NEO4J_PASSWORD"] = Password,
            ["ALPHA_TOKEN"] = new string('x', 40), ["BETA_TOKEN"] = new string('y', 40),
        };
        var config = CodingConfiguration.Parse($$"""{"ownerId":"owner","projects":{"alpha":"ALPHA_TOKEN","beta":"BETA_TOKEN"},"url":"http://127.0.0.1:{{port}}"}""",
            environment.GetValueOrDefault);
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        CodingHost.Configure(builder, config);
        var services = builder.Services;
        services.RemoveAll<IChatClient>();
        var chat = Substitute.For<IChatClient>();
        services.AddSingleton(chat);
        services.RemoveAll<IEmbeddingGenerator<string, Embedding<float>>>();
        services.AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>(new StubEmbeddingGenerator(
            NullLogger<StubEmbeddingGenerator>.Instance, Dimensions));
        var model = new CodingTestWriter();
        services.RemoveAll<IMemoryWriter>();
        services.AddSingleton<IMemoryWriter>(model);
        await using var app = builder.Build();
        await CodingHost.PrepareAsync(app, config);
        CodingTransport.Map(app);
        var scopes = app.Services.GetRequiredService<CodingOperationScope>();
        var operations = app.Services.GetRequiredService<CodingMemoryOperations>();
        var writer = app.Services.GetRequiredService<ICodingWriter>();
        var alpha = new CodingIdentity("owner", "alpha");
        var beta = new CodingIdentity("owner", "beta");

        var request = new CodingRememberRequest
        {
            Session = "same-client-session", TurnId = "same-turn", UserText = "alpha-choice",
            Commit = new string('a', 40), Source = "settings.cs",
        };
        await app.StartAsync();
        using var client = new HttpClient { BaseAddress = new Uri(config.Options.Url) };
        CodingWriteJob alphaJob;
        try
        {
            var first = await SubmitAsync("alpha", request);
            var second = await SubmitAsync("beta", request with { UserText = "beta-choice" });
            alphaJob = await AwaitCodingJobAsync(client, "alpha", environment["ALPHA_TOKEN"], first.JobId);
            alphaJob.State.Should().Be(CodingJobState.Succeeded, alphaJob.ErrorCode);
            var betaJob = await AwaitCodingJobAsync(client, "beta", environment["BETA_TOKEN"], second.JobId);
            betaJob.State.Should().Be(CodingJobState.Succeeded, betaJob.ErrorCode);
            using var foreign = new HttpRequestMessage(HttpMethod.Post, "/projects/beta/write_status")
                { Content = JsonContent.Create(new { jobId = first.JobId }) };
            foreign.Headers.Authorization = new("Bearer", environment["BETA_TOKEN"]);
            using var denied = await client.SendAsync(foreign);
            denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
        finally { await app.StopAsync(); }

        var alphaFact = (await operations.HistoryAsync(alpha)).Should().ContainSingle().Subject;
        var betaFact = (await operations.HistoryAsync(beta)).Should().ContainSingle().Subject;
        alphaFact.Summary.Should().Contain("alpha-choice").And.NotContain("beta-choice");
        betaFact.Summary.Should().Contain("beta-choice").And.NotContain("alpha-choice");
        alphaFact.OwnerId.Should().Be("owner");
        alphaFact.SourceMessageIds.Should().Contain(alphaJob.SourceMessageId!);
        var source = await scopes.RunAsync(alpha, (sp, ct) =>
            sp.GetRequiredService<IMessageRepository>().GetByIdAsync(alphaJob.SourceMessageId!, ct));
        source!.Metadata["coding_commit"].ToString().Should().Be(request.Commit);
        source.Metadata["coding_source"].ToString().Should().Be(request.Source);
        (await scopes.RunAsync(beta, (sp, ct) =>
            sp.GetRequiredService<IMessageRepository>().GetByIdAsync(alphaJob.SourceMessageId!, ct))).Should().BeNull();

        // A new queue/process cannot infer completion from the source alone; no second writer call.
        (await writer.WriteAsync(alpha, request)).PriorAttemptUnknown.Should().BeTrue();
        model.Calls.Should().Be(2);
        chat.ReceivedCalls().Where(c => c.GetMethodInfo().Name is "GetResponseAsync" or "GetStreamingResponseAsync").Should().BeEmpty();
        (await operations.ForgetAsync(beta, MemoryHistoryKind.Fact, alphaFact.Id)).Should().BeFalse();
        (await operations.ForgetAsync(alpha, MemoryHistoryKind.Fact, alphaFact.Id)).Should().BeTrue();
        (await operations.HistoryAsync(alpha, alphaFact.Id)).Should().ContainSingle()
            .Which.Status.Should().Be(MemoryHistoryStatus.Invalidated);
        (await operations.HistoryAsync(beta, alphaFact.Id)).Should().BeEmpty();
        (await operations.HistoryAsync(beta, betaFact.Id)).Should().ContainSingle()
            .Which.Status.Should().Be(MemoryHistoryStatus.Live);

        async Task<CodingWriteJob> SubmitAsync(string project, CodingRememberRequest value)
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, $"/projects/{project}/remember")
                { Content = JsonContent.Create(value, options: CodingApi.Json) };
            message.Headers.Authorization = new("Bearer", environment[project.ToUpperInvariant() + "_TOKEN"]);
            using var response = await client.SendAsync(message);
            response.StatusCode.Should().Be(HttpStatusCode.Accepted);
            return (await response.Content.ReadFromJsonAsync<CodingWriteJob>(CodingApi.Json))!;
        }
    }

    private static async Task<CodingWriteJob> AwaitCodingJobAsync(HttpClient client, string project, string token, string id)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        while (true)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"/projects/{project}/write_status")
                { Content = JsonContent.Create(new { jobId = id }) };
            request.Headers.Authorization = new("Bearer", token);
            using var response = await client.SendAsync(request, deadline.Token);
            response.EnsureSuccessStatusCode();
            var job = (await response.Content.ReadFromJsonAsync<CodingWriteJob>(CodingApi.Json, deadline.Token))!;
            if (job.State is not (CodingJobState.Queued or CodingJobState.Running)) return job;
            await Task.Delay(20, deadline.Token);
        }
    }

    private sealed class CodingTestWriter : IMemoryWriter
    {
        public bool IsEnabled => true;
        public int Calls { get; private set; }
        public Task<UnifiedExtractionResult> WriteAsync(MemoryWriteRequest request, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new UnifiedExtractionResult
            {
                Facts = [new ExtractedFact { Subject = "Project", Predicate = "uses", Object = request.Window.Targets.Single(m => m.Role == "user").Content }],
            });
        }
    }
}
