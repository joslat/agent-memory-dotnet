using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Routing;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>G5 (40.49): the two gates the real-store test does not reach, the router and valid time.</summary>
public sealed class MemoryRecallExplainerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 3, 12, 0, 0, TimeSpan.Zero);

    private static Fact Fact(DateTimeOffset? validUntil = null) => new()
    {
        FactId = "f1", Subject = "user", Predicate = "has", Object = "a dentist appointment", Confidence = 0.9,
        CreatedAtUtc = Now.AddDays(-30), OwnerId = "ana", ValidUntil = validUntil,
    };

    private static MemoryRecallExplainer Sut(Fact fact, MemoryOptions options)
    {
        var facts = Substitute.For<IFactRepository>();
        facts.GetByIdAsync("f1", Arg.Any<CancellationToken>()).Returns(fact);
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(Now);
        return new MemoryRecallExplainer(Substitute.For<IMemoryService>(), facts, Substitute.For<IEmbeddingOrchestrator>(),
            Options.Create(options), clock, new RuleBasedMemoryRouter(options.Routing));
    }

    private static RecallRequest Ask(string text, RecallOptions? recall = null) => new()
    {
        SessionId = "s", UserId = "ana", Query = text, Options = recall ?? RecallOptions.Default,
    };

    [Fact]
    public async Task With_routing_on_a_statement_keeps_every_fact_out()
    {
        var options = new MemoryOptions();
        options.Routing.Enabled = true;

        var why = await Sut(Fact(), options).WhyNotFactAsync(Ask("I have a dentist appointment on Friday."), "f1");

        why.Gate.Should().Be(MemoryWhyNot.Router);
        why.Detail.Should().Contain("statement");
    }

    [Fact]
    public async Task A_fact_past_its_window_is_kept_out_when_recall_reads_valid_time()
    {
        var why = await Sut(Fact(validUntil: Now.AddDays(-1)), new MemoryOptions())
            .WhyNotFactAsync(Ask("When is my dentist appointment?", RecallOptions.Default with { ValidTime = ValidTimeMode.Current }), "f1");

        why.Gate.Should().Be(MemoryWhyNot.Validity);
    }
}
