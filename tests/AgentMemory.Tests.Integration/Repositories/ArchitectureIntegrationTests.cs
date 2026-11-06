#pragma warning disable AMEXT001
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Storage;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Packs.Architecture;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AgentMemory.Tests.Integration.Repositories;

/// <summary>
/// The architecture pack on a real Neo4j (CMP-5): a repository's catalog and API document imported as a snapshot, and the walk
/// it declares: from an operation id named in code to its API, and from the API to the component that provides it.
/// </summary>
public sealed partial class ModuleRecordWriteIntegrationTests
{
    private static ModuleSnapshot NoArchitecture(string source) => new() { Module = ArchitecturePack.Manifest, Source = source, Records = [] };

    [Fact]
    public async Task AnArchitectureSnapshotImportsOnceAndLeadsFromAnOperationIdToTheComponentThatProvidesIt()
    {
        var root = Directory.CreateTempSubdirectory("architecture-it-").FullName;
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "api"));
            File.WriteAllText(Path.Combine(root, "catalog-info.yaml"), string.Join('\n',
                "apiVersion: backstage.io/v1alpha1", "kind: Component", "metadata:", "  name: orders-service", "spec:", "  type: service",
                "  owner: team-shop", "  providesApis: [orders-api]", "---", "apiVersion: backstage.io/v1alpha1", "kind: API", "metadata:",
                "  name: orders-api", "spec:", "  type: openapi", "  owner: team-shop", "  definition:", "    $text: ./api/orders.yaml", ""));
            File.WriteAllText(Path.Combine(root, "api", "orders.yaml"), string.Join('\n',
                "openapi: 3.0.3", "info: {title: Orders, version: 1.0.0}", "paths:", "  /orders:", "    post: {operationId: CreateOrder, summary: Place an order}", ""));
            var snapshot = ArchitectureReader.Read(root, ["catalog-info.yaml"], "git:one", out var skipped);
            skipped.Should().BeEmpty();

            var store = new Neo4jModuleSnapshotStore(fixture.TransactionRunner);
            var at = DateTimeOffset.UtcNow.AddMinutes(6);
            await store.ImportAsync(NoArchitecture("git:clear"), at.AddSeconds(-1));
            (await store.ImportAsync(snapshot, at)).Should().Be(new ModuleSnapshotResult { ModuleId = ArchitecturePack.Id, Source = "git:one", Created = snapshot.Records.Count });
            (await store.ImportAsync(snapshot with { Source = "git:two" }, at.AddSeconds(1)))
                .Should().Be(new ModuleSnapshotResult { ModuleId = ArchitecturePack.Id, Source = "git:two", Unchanged = snapshot.Records.Count });

            using var services = Policies();
            var reader = new Neo4jModuleTraversalReader(fixture.TransactionRunner, KnowledgePack.ResolutionOf(ArchitecturePack.Manifest),
                services.GetRequiredService<IMemoryIsolationPolicy>());
            var context = new ModuleReadContext { Scope = MemoryScope.For("lea") };
            var operation = (await reader.MatchAsync(new() { ModuleId = ArchitecturePack.Id, Type = ArchitecturePack.OperationType, Property = "operationId",
                Match = ModuleAnchorMatch.Exact, Values = ["CreateOrder"] }, context)).Records.Should().ContainSingle().Subject;
            var api = (await reader.ExpandAsync(new() { ModuleId = ArchitecturePack.Id, Relation = ArchitecturePack.OperationOf,
                Direction = ModuleTraversalDirection.Out, FromIds = [operation.Id] }, context)).Edges.Should().ContainSingle().Subject.To;
            api.Properties["file"].GetString().Should().Be("api/orders.yaml");
            var component = (await reader.ExpandAsync(new() { ModuleId = ArchitecturePack.Id, Relation = ArchitecturePack.Provides,
                Direction = ModuleTraversalDirection.In, FromIds = [api.Id] }, context)).Edges.Should().ContainSingle().Subject.To;
            component.Properties["name"].GetString().Should().Be("orders-service");

            await store.ImportAsync(NoArchitecture("git:clear"), at.AddSeconds(2));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
