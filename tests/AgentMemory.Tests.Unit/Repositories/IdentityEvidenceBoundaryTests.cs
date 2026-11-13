using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.Neo4j.Repositories;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Repositories;

public sealed class IdentityEvidenceBoundaryTests
{
    private static (Neo4jIdentityEvidenceRepository Repository, INeo4jTransactionRunner Runner) Create(bool enabled = true)
    {
        var runner = Substitute.For<INeo4jTransactionRunner>(); var options = new Neo4jOptions();
        if (enabled) options.Extensions.Add("identity-evidence");
        return (new(runner, Options.Create(options)), runner);
    }
    private static IdentityCandidateQuery Query() => new() { Name = "Jose", Type = "PERSON", Limit = 12 };
    [Fact]
    public async Task Disabled_backend_performs_no_reads_writes_or_model_work()
    {
        var (repository, runner) = Create(false);
        var read = () => repository.FindExactCandidatesAsync(Query());
        var approximate = () => repository.FindApproximateCandidatesAsync(Query(), [1, 0]);
        var write = () => repository.AssertNameNamespaceAsync("entity", "staff", "directory", "source");
        await read.Should().ThrowAsync<InvalidOperationException>(); await approximate.Should().ThrowAsync<InvalidOperationException>();
        await write.Should().ThrowAsync<InvalidOperationException>(); runner.ReceivedCalls().Should().BeEmpty();
    }
    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(129)]
    public async Task Invalid_candidate_bounds_fail_before_database_work(int limit)
    {
        var (repository, runner) = Create(); var action = () => repository.FindExactCandidatesAsync(Query() with { Limit = limit });
        await action.Should().ThrowAsync<ArgumentException>(); runner.ReceivedCalls().Should().BeEmpty();
    }
    [Fact]
    public async Task Unsupported_name_comparison_does_not_silently_use_neo4j_case_folding()
    {
        var (repository, runner) = Create(); var action = () => repository.FindExactCandidatesAsync(Query() with { IdentityNameComparison = StringComparison.OrdinalIgnoreCase });
        await action.Should().ThrowAsync<NotSupportedException>(); runner.ReceivedCalls().Should().BeEmpty();
    }
    [Fact]
    public async Task Cancelled_call_does_no_database_work()
    {
        var (repository, runner) = Create(); var action = () => repository.FindExactCandidatesAsync(Query(), new CancellationToken(true));
        await action.Should().ThrowAsync<OperationCanceledException>(); runner.ReceivedCalls().Should().BeEmpty();
    }
    [Theory]
    [InlineData("missing")] [InlineData("Employee_id")]
    public async Task Key_components_are_complete_and_ordinal(string property)
    {
        var (repository, runner) = Create();
        var action = () => repository.AssertKeyAsync("entity", new VerifiedIdentityKey { Namespace = "staff", Authority = "directory", SourceId = "source",
            Values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["employee_id"] = "42" } }, [property]);
        await action.Should().ThrowAsync<ArgumentException>(); runner.ReceivedCalls().Should().BeEmpty();
    }
    [Fact]
    public async Task Disabling_both_candidate_routes_skips_the_transaction()
    {
        var (repository, runner) = Create();
        (await repository.FindApproximateCandidatesAsync(Query() with { UseFuzzyCandidates = false }, null)).Candidates.Should().BeEmpty();
        runner.ReceivedCalls().Should().BeEmpty();
    }
    [Fact]
    public async Task Unknown_kind_cannot_be_registered_as_verified()
    {
        var (repository, runner) = Create(); var action = () => repository.AssertKindAsync("entity", new() { Kind = IdentitySubjectKind.Unknown, Authority = "directory", SourceId = "source" });
        await action.Should().ThrowAsync<ArgumentException>(); runner.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task An_incomplete_query_key_fails_before_opening_a_read_transaction()
    {
        var (repository, runner) = Create();
        var query = Query() with { IdentityProperties = ["employee_id"], VerifiedKeys = [new()
        { Namespace = "staff", Authority = "directory", SourceId = "source", Values = new Dictionary<string, string>() }] };
        var action = () => repository.FindExactCandidatesAsync(query);
        await action.Should().ThrowAsync<ArgumentException>();
        runner.ReceivedCalls().Should().BeEmpty();
    }
}
