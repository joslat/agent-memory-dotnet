using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Resolution;
using FluentAssertions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Resolution;

/// <summary>Context rejection must have bounded allocation, including heavily escaped source text.</summary>
public sealed class IdentityContextBudgetRegressionTests
{
    [Fact]
    public async Task Oversize_context_is_rejected_without_materializing_its_complete_JSON()
    {
        var model = Substitute.For<IIdentityAdjudicator>();
        var configuration = IdentityConfiguration.Capture(new IdentityResolutionOptions
        { Enabled = true, MaxContextUtf8Bytes = 1_048_576 });
        var mention = new ExtractedEntity { Name = "Alex", Type = "PERSON" };
        var entity = new Entity { EntityId = "observation", Name = "Alex", Type = "PERSON",
            Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch };
        // Escaping increases JSON size. Construct the input before measuring the rejection itself.
        var sourceText = new string('\u00f1', 65_536);
        var plan = new IdentityResolutionPlan(entity, true,
            new(IdentityDecisionKind.Pending, null, ["candidate"], [], true,
                new Dictionary<string, string> { ["candidate"] = "candidate_for_review" }),
            [new IdentityCandidate { Entity = entity with { EntityId = "candidate", Name = "Alexandra" } }],
            new EntityIdentityResolutionContext
            {
                SourceMessageIds = ["root"],
                Sources = Enumerable.Range(0, 32).Select(index => new IdentitySourceFragment
                { SourceId = "source:" + index, Text = sourceText, OriginIds = ["root"] }).ToArray()
            }, []);
        var sut = new IdentityContextualAdjudication(model, configuration, TimeProvider.System);
        var before = GC.GetAllocatedBytesForCurrentThread();
        var operation = sut.ApplyAsync(mention, plan, new IdentityAdjudicationBudget(1));
        // Budget rejection is synchronous; no thread switch distorts the allocation measurement.
        operation.IsCompleted.Should().BeTrue();
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        var result = await operation;
        result.Diagnostics.Should().Contain("contextual_context_budget");
        result.Entity.EntityId.Should().Be("observation");
        allocated.Should().BeLessThan(8L * configuration.MaxContextUtf8Bytes,
            "rejecting an oversize prompt must have bounded allocation, independent of the complete source JSON");
        await model.DidNotReceive().GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>());
    }

    private static readonly ExtractedEntity Mention = new() { Name = "Alex", Type = "PERSON" };
    private const string Uncertain = """{"decision":"uncertain","candidateId":null,"evidence":[]}""";
    private static IdentityResolutionPlan Plan(string source = "Alex is Alexandra")
    {
        var entity = new Entity { EntityId = "observation", Name = "Alex", Type = "PERSON", Confidence = 1,
            SourceMessageIds = ["root"], CreatedAtUtc = DateTimeOffset.UnixEpoch };
        return new(entity, true, new(IdentityDecisionKind.Pending, null, ["candidate"], [], true,
            new Dictionary<string, string> { ["candidate"] = "candidate_for_review" }),
            [new IdentityCandidate { Entity = entity with { EntityId = "candidate", Name = "Alexandra" } }],
            new() { SourceMessageIds = ["root"], Sources = [new() { SourceId = "root", Text = source, OriginIds = ["root"] }] }, []);
    }

    [Fact]
    public async Task Only_configured_scalar_context_properties_reach_the_model()
    {
        var model = Substitute.For<IIdentityAdjudicator>(); IdentityAdjudicationRequest? request = null;
        model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => { request = call.Arg<IdentityAdjudicationRequest>(); return Uncertain; });
        var options = new IdentityResolutionOptions { Enabled = true };
        options.Policies["PERSON"] = new() { ContextProperties = ["country", "requires_physical_presence"] };
        var mention = Mention with { Attributes = new Dictionary<string, object>
            { ["country"] = "Argentina", ["requires_physical_presence"] = true, ["unselected"] = "private-unselected-value" } };
        var plan = Plan(); plan = plan with { Candidates = [plan.Candidates[0] with { Entity = plan.Candidates[0].Entity with
            { Attributes = new Dictionary<string, object> { ["country"] = "Spain", ["unselected"] = "candidate-unselected-value" } } }] };
        await new IdentityContextualAdjudication(model, IdentityConfiguration.Capture(options), TimeProvider.System)
            .ApplyAsync(mention, plan, new IdentityAdjudicationBudget(1));
        using var json = JsonDocument.Parse(request!.DataJson);
        var own = json.RootElement.GetProperty("mention").GetProperty("contextProperties");
        own.GetProperty("country").GetString().Should().Be("Argentina");
        own.GetProperty("requires_physical_presence").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("candidates")[0].GetProperty("contextProperties").GetProperty("country").GetString().Should().Be("Spain");
        request.DataJson.Should().NotContain("unselected-value");
        request.SystemInstructions.Should().NotContain("Argentina").And.NotContain("Spain");
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task An_unsupported_configured_property_keeps_identity_pending_without_invoking_getters(bool onCandidate)
    {
        var model = Substitute.For<IIdentityAdjudicator>();
        model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(Uncertain);
        var options = new IdentityResolutionOptions { Enabled = true };
        options.Policies["PERSON"] = new() { ContextProperties = ["country"] };
        var poison = new Poison(); var mention = Mention; var plan = Plan();
        if (onCandidate) plan = plan with { Candidates = [plan.Candidates[0] with { Entity = plan.Candidates[0].Entity with
            { Attributes = new Dictionary<string, object> { ["country"] = poison } } }] };
        else mention = mention with { Attributes = new Dictionary<string, object> { ["country"] = poison } };
        var result = await new IdentityContextualAdjudication(model, IdentityConfiguration.Capture(options), TimeProvider.System)
            .ApplyAsync(mention, plan, new IdentityAdjudicationBudget(1));
        result.Diagnostics.Should().Contain("contextual_invalid_property");
        result.Result.Decision.Should().Be(IdentityDecisionKind.Pending); poison.GetterCalls.Should().Be(0);
        await model.DidNotReceive().GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unselected_attributes_are_never_serialized_or_executed()
    {
        var model = Substitute.For<IIdentityAdjudicator>();
        model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(Uncertain);
        var poison = new Poison();
        var mention = Mention with { Attributes = new Dictionary<string, object> { ["unselected"] = poison } };
        await new IdentityContextualAdjudication(model, IdentityConfiguration.Capture(new() { Enabled = true }), TimeProvider.System)
            .ApplyAsync(mention, Plan(), new IdentityAdjudicationBudget(1));
        poison.GetterCalls.Should().Be(0);
        await model.Received(1).GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Utf8_limit_includes_json_overhead_and_accepts_the_exact_boundary(bool exceedByOne)
    {
        var model = Substitute.For<IIdentityAdjudicator>(); IdentityAdjudicationRequest? sent = null;
        model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>())
            .Returns(call => { sent = call.Arg<IdentityAdjudicationRequest>(); return Uncertain; });
        var plan = Plan(new string('a', 6000));
        var options = new IdentityResolutionOptions { Enabled = true, MaxContextUtf8Bytes = 65536 };
        await new IdentityContextualAdjudication(model, IdentityConfiguration.Capture(options), TimeProvider.System)
            .ApplyAsync(Mention, plan, new IdentityAdjudicationBudget(1));
        var actualBytes = System.Text.Encoding.UTF8.GetByteCount(sent!.DataJson);
        options.MaxContextUtf8Bytes = actualBytes - (exceedByOne ? 1 : 0); model.ClearReceivedCalls();
        var result = await new IdentityContextualAdjudication(model, IdentityConfiguration.Capture(options), TimeProvider.System)
            .ApplyAsync(Mention, plan, new IdentityAdjudicationBudget(1));
        if (exceedByOne)
        {
            result.Diagnostics.Should().Contain("contextual_context_budget");
            await model.DidNotReceive().GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>());
        }
        else await model.Received(1).GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Cancellation_during_context_preparation_never_spends_a_model_call()
    {
        using var cancellation = new CancellationTokenSource();
        var model = Substitute.For<IIdentityAdjudicator>();
        model.GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>()).Returns(Uncertain);
        var plan = Plan(); plan = plan with { Context = plan.Context with
            { Sources = new CancellingSources(plan.Context.Sources[0], cancellation) } };
        var budget = new IdentityAdjudicationBudget(1);
        var sut = new IdentityContextualAdjudication(model, IdentityConfiguration.Capture(new() { Enabled = true }), TimeProvider.System);
        var action = () => sut.ApplyAsync(Mention, plan, budget, cancellation.Token);
        await action.Should().ThrowAsync<OperationCanceledException>();
        budget.Consumed.Should().Be(0);
        await model.DidNotReceive().GetDecisionAsync(Arg.Any<IdentityAdjudicationRequest>(), Arg.Any<CancellationToken>());
    }

    private sealed class CancellingSources(IdentitySourceFragment source, CancellationTokenSource cancellation)
        : IReadOnlyList<IdentitySourceFragment>
    {
        public int Count => 1;
        public IdentitySourceFragment this[int index] => index == 0 ? source : throw new ArgumentOutOfRangeException(nameof(index));
        public IEnumerator<IdentitySourceFragment> GetEnumerator() { cancellation.Cancel(); yield return source; }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }

    private sealed class Poison
    {
        public int GetterCalls;
        public string Value { get { GetterCalls++; throw new InvalidOperationException("Host getters are not context data."); } }
    }
}
