using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Architecture;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The architecture pack (CMP-5): a repository's Backstage catalog, its API documents, an SBOM and a Structurizr workspace read
/// into one snapshot, deterministically; one component described in two files is one record; whatever a record links to is a
/// record; a YAML file that expands without bound is left out.
/// </summary>
public sealed class ArchitecturePackTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("architecture-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Write(string path, string text)
    {
        var full = Path.Combine(_root, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text.ReplaceLineEndings("\n"));
    }

    private void Repository()
    {
        Write("catalog-info.yaml", """
            apiVersion: backstage.io/v1alpha1
            kind: System
            metadata:
              name: shop
              description: The online shop.
            spec:
              owner: team-shop
            ---
            apiVersion: backstage.io/v1alpha1
            kind: Component
            metadata:
              name: orders-service
              description: Takes and tracks orders.
              annotations:
                backstage.io/source-location: dir:./src/Orders
            spec:
              type: service
              lifecycle: production
              owner: team-shop
              system: shop
              providesApis: [orders-api]
              consumesApis: [payments-api]
              dependsOn: [resource:orders-db]
            ---
            apiVersion: backstage.io/v1alpha1
            kind: API
            metadata:
              name: orders-api
            spec:
              type: openapi
              owner: team-shop
              system: shop
              definition:
                $text: ./api/orders.yaml
            ---
            apiVersion: backstage.io/v1alpha1
            kind: Group
            metadata:
              name: team-shop
            spec:
              profile:
                displayName: Shop team
            """);
        Write("api/orders.yaml", """
            openapi: 3.0.3
            info: {title: Orders, version: 1.2.0}
            paths:
              /orders:
                get: {operationId: ListOrders, summary: List orders}
                post: {operationId: CreateOrder, summary: Place an order}
              /orders/{id}:
                get: {operationId: GetOrder}
            """);
        Write("events.asyncapi.yaml", """
            asyncapi: 2.6.0
            info: {title: Order events, version: 1.0.0}
            channels:
              orders.created:
                publish: {operationId: OrderCreated, summary: An order was placed}
            """);
        Write("bom.cdx.json", """
            {"bomFormat":"CycloneDX","specVersion":"1.5","metadata":{"component":{"name":"orders-service"}},
             "components":[{"type":"library","name":"Newtonsoft.Json","version":"13.0.3","purl":"pkg:nuget/Newtonsoft.Json@13.0.3","licenses":[{"license":{"id":"MIT"}}]}]}
            """);
        Write("docs/workspace.json", """
            {"model":{"softwareSystems":[{"id":"1","name":"Shop","containers":[
              {"id":"2","name":"Orders Service","technology":".NET"},
              {"id":"3","name":"Web Shop","technology":"React","relationships":[{"sourceId":"3","destinationId":"2"}]}]}]}}
            """);
        Write("openapi-bomb.yaml", "a: &a [x, x, x, x, x, x, x, x, x, x]\nb: &b [*a, *a, *a, *a, *a, *a, *a, *a, *a, *a]\nc: &c [*b, *b, *b, *b, *b, *b, *b, *b, *b, *b]\n"
            + "d: &d [*c, *c, *c, *c, *c, *c, *c, *c, *c, *c]\ne: &e [*d, *d, *d, *d, *d, *d, *d, *d, *d, *d]\nf: [*e, *e, *e, *e, *e, *e, *e, *e, *e, *e]\n");
        Write("src/Orders/OrdersController.cs", "class OrdersController {}");
    }

    private ModuleSnapshot Read(out IReadOnlyList<string> skipped)
    {
        var files = Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(_root, f).Replace('\\', '/'));
        return ArchitectureReader.Read(_root, ArchitectureReader.ArchitectureFiles(files), "scan", out skipped);
    }

    private static ModuleProposal Record(ModuleSnapshot snapshot, string key) => snapshot.Records.Single(r => r.Key == key);

    private static string Text(ModuleProposal record, string property) => record.Properties[property].ToString();

    private static IEnumerable<(string, string)> Links(ModuleProposal record) => record.Links.Select(l => (l.Relation, l.Target));

    [Fact]
    public void Candidate_files_are_found_by_name_and_a_catalogs_API_document_through_the_catalog()
    {
        ArchitectureReader.ArchitectureFiles(["catalog-info.yaml", "api/orders.yaml", "events.asyncapi.yaml", "bom.cdx.json", "docs/workspace.json", "src/a.cs", "openapi.json", "swagger.yml", "x.spdx.json"])
            .Should().Equal("bom.cdx.json", "catalog-info.yaml", "docs/workspace.json", "events.asyncapi.yaml", "openapi.json", "swagger.yml", "x.spdx.json");
    }

    [Fact]
    public void A_repository_reads_into_one_snapshot_that_the_manifest_accepts()
    {
        Repository();

        var snapshot = Read(out var skipped);

        snapshot.Problems().Should().BeEmpty();
        skipped.Should().ContainSingle().Which.Should().StartWith("openapi-bomb.yaml").And.Contain("expands to more than");

        var orders = Record(snapshot, "component:default/orders-service");
        Text(orders, "kind").Should().Be("service");
        Text(orders, "technology").Should().Be(".NET", "the workspace's container is the same component, by name");
        Text(orders, "path").Should().Contain("src/Orders/**");
        Links(orders).Should().BeEquivalentTo([
            (ArchitecturePack.PartOf, "system:default/shop"), (ArchitecturePack.Provides, "api:default/orders-api"),
            (ArchitecturePack.Consumes, "api:default/payments-api"), (ArchitecturePack.DependsOn, "resource:default/orders-db"),
            (ArchitecturePack.ComponentOwnedBy, "group:default/team-shop"), (ArchitecturePack.UsesPackage, "pkg:nuget/Newtonsoft.Json@13.0.3")]);

        var api = Record(snapshot, "api:default/orders-api");
        Text(api, "kind").Should().Be("openapi");
        Text(api, "file").Should().Be("api/orders.yaml");
        Text(api, "operations").Should().Be("3");
        Text(Record(snapshot, "api:default/orders-api#POST /orders"), "operationId").Should().Be("CreateOrder");
        Links(Record(snapshot, "api:default/orders-api#GET /orders/{id}")).Should().Equal((ArchitecturePack.OperationOf, "api:default/orders-api"));

        Text(Record(snapshot, "asyncapi:events.asyncapi.yaml#publish orders.created"), "summary").Should().Be("An order was placed");
        Text(Record(snapshot, "group:default/team-shop"), "description").Should().Be("Shop team");
        Text(Record(snapshot, "resource:default/orders-db"), "kind").Should().Be("resource", "named only, so the link has a target");
        Text(Record(snapshot, "pkg:nuget/Newtonsoft.Json@13.0.3"), "licenses").Should().Be("MIT");
        Links(Record(snapshot, "component:default/web-shop")).Should().Contain((ArchitecturePack.DependsOn, "component:default/orders-service"));
    }

    [Fact]
    public void Reading_twice_gives_the_same_snapshot()
    {
        Repository();
        var first = Read(out _);
        var second = Read(out _);

        second.Records.Select(r => (r.Type, r.Key, string.Join("|", r.Properties.Select(p => p.Key + "=" + p.Value)), string.Join("|", Links(r))))
            .Should().Equal(first.Records.Select(r => (r.Type, r.Key, string.Join("|", r.Properties.Select(p => p.Key + "=" + p.Value)), string.Join("|", Links(r)))));
    }

    [Fact]
    public void The_manifest_declares_a_walk_from_documents_operations_and_source_folders()
    {
        var walk = ArchitecturePack.Manifest.Traversal!;
        walk.Anchors.Select(a => (a.Type, a.Property)).Should().BeEquivalentTo([
            (ArchitecturePack.ApiType, "file"), (ArchitecturePack.OperationType, "operationId"), (ArchitecturePack.ComponentType, "path")]);
        ArchitecturePack.Manifest.Views.Select(v => v.Id).Should().Contain(["components", "apis", "operations", "licences"]);
    }
}
