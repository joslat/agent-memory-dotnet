using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

public sealed class IdentityOwnerCompatibilityTests
{
    private sealed class Fixture
    {
        public readonly INeo4jTransactionRunner Tx = Substitute.For<INeo4jTransactionRunner>();
        public readonly IEmbeddingOrchestrator Embeddings = Substitute.For<IEmbeddingOrchestrator>();
        public readonly List<string> Queries = [];
        public readonly Neo4jMemoryOwnerDataService Service;
        public Fixture(bool enabled)
        {
            var options = new Neo4jOptions(); if (enabled) options.Extensions.Add("identity-evidence");
            Service = new(Tx, Substitute.For<IEntityRepository>(), Substitute.For<IFactRepository>(),
                Substitute.For<IPreferenceRepository>(), Substitute.For<IRelationshipRepository>(), Embeddings,
                Substitute.For<IIdGenerator>(), TimeProvider.System, Options.Create(options));
            IAsyncQueryRunner Runner()
            {
                var runner = Substitute.For<IAsyncQueryRunner>();
                IResultCursor Read(string query)
                {
                    Queries.Add(query); var cursor = Substitute.For<IResultCursor>();
                    cursor.FetchAsync().Returns(false);
                    cursor.Current.Returns(Substitute.For<IRecord>()); return cursor;
                }
                runner.RunAsync(Arg.Any<string>(), Arg.Any<object>()).Returns(c => Read(c.Arg<string>()));
                runner.RunAsync(Arg.Any<string>(), Arg.Any<IDictionary<string, object>>()).Returns(c => Read(c.Arg<string>()));
                return runner;
            }
            Tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<OwnerMemoryExport>>>(), Arg.Any<CancellationToken>())
                .Returns(c => c.Arg<Func<IAsyncQueryRunner, Task<OwnerMemoryExport>>>()(Runner()));
            Tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
                .Returns(c => c.Arg<Func<IAsyncQueryRunner, Task>>()(Runner()));
        }
    }

    [Fact]
    public async Task Existing_identity_refuses_import_before_writes_or_embeddings()
    {
        var f = new Fixture(true);
        Func<Task> action = async () => { await f.Service.ImportAsync(new() { OwnerId = "source", ExportedAtUtc = DateTimeOffset.UnixEpoch }, "owner"); };
        await action.Should().ThrowAsync<NotSupportedException>().WithMessage("*agentmemory-export/1*");
        f.Queries.Should().BeEmpty();
        f.Tx.ReceivedCalls().Should().BeEmpty();
        f.Embeddings.ReceivedCalls().Should().BeEmpty();
        f.Tx.ReceivedCalls().Should().NotContain(c => c.GetMethodInfo().Name == "WriteAsync");
    }

    [Fact]
    public async Task Disabled_identity_keeps_the_six_legacy_export_queries_and_empty_import()
    {
        var f = new Fixture(false);
        var export = await f.Service.ExportAsync("owner");
        export.Format.Should().Be(OwnerMemoryExport.CurrentFormat); export.Facts.Should().BeEmpty();
        f.Queries.Count.Should().Be(6, "disabled identity adds no query");
        (await f.Service.ImportAsync(export, "new-owner")).Written.Should().BeEmpty();
        f.Queries.Count.Should().Be(6);
    }

    [Fact]
    public async Task Existing_identity_exports_for_reading_with_its_resolutions_and_an_omission_flag()
    {
        // ILC-18 interim: an owner's memory stays readable with identity enabled; the export says what it leaves out.
        var f = new Fixture(true);
        var export = await f.Service.ExportAsync("owner");
        export.IdentityStateOmitted.Should().BeTrue();
        export.IdentityResolutions.Should().NotBeNull();
        f.Queries.Count.Should().Be(7, "the six memory queries plus one identity-resolution read");
        f.Embeddings.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task An_export_that_omits_identity_state_cannot_be_imported_even_without_identity()
    {
        var f = new Fixture(false);
        var action = () => f.Service.ImportAsync(new() { OwnerId = "source", ExportedAtUtc = DateTimeOffset.UnixEpoch, IdentityStateOmitted = true }, "owner");
        await action.Should().ThrowAsync<NotSupportedException>();
        f.Queries.Should().BeEmpty(); f.Embeddings.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Disabled_identity_export_serializes_without_any_identity_field()
    {
        var f = new Fixture(false);
        var json = System.Text.Json.JsonSerializer.Serialize(await f.Service.ExportAsync("owner"));
        json.Should().NotContain("IdentityResolutions").And.NotContain("IdentityStateOmitted");
    }

    [Fact]
    public async Task Cancellation_and_unknown_formats_fail_without_any_portability_query()
    {
        var f = new Fixture(true);
        var cancelled = () => f.Service.ImportAsync(new() { OwnerId = "source", ExportedAtUtc = DateTimeOffset.UnixEpoch }, "owner", new CancellationToken(true));
        var unknown = () => f.Service.ImportAsync(new() { Format = "unsupported/2", OwnerId = "source", ExportedAtUtc = DateTimeOffset.UnixEpoch }, "owner");
        await cancelled.Should().ThrowAsync<OperationCanceledException>(); await unknown.Should().ThrowAsync<ArgumentException>();
        f.Queries.Should().BeEmpty();
    }
}
