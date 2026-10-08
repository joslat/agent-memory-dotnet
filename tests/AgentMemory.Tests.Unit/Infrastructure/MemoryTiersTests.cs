using System.Net;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Extraction;
using AgentMemory.Core.Tiers;
using AgentMemory.Gate;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// 41.26 (d): each leg of memory says which tier it runs at (storage, retrieval, dreaming), on one line the library logs
/// once on its first write and a host can ask for (IMemoryTiers).
/// </summary>
public sealed class MemoryTiersTests
{
    private static readonly SystemOneEndpoint Jev = new() { Name = "jev", Endpoint = new("https://jev.test/v1/systemone") };

    private static IMemoryTiers Tiers(Action<IServiceCollection>? more = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(_ => { });
        more?.Invoke(services);
        return services.BuildServiceProvider().GetRequiredService<IMemoryTiers>();
    }

    private static void Writer(IServiceCollection services)
    {
        var writer = Substitute.For<IMemoryWriter>();
        writer.IsEnabled.Returns(true);
        services.AddSingleton(writer);
    }

    private static void Gate(IServiceCollection services, Action<MemoryGateOptions> configure)
    {
        services.AddSingleton(Substitute.For<IMemoryContextAssembler>());
        services.AddAgentMemoryGate(configure);
    }

    [Fact]
    public void Without_the_winners_it_is_the_extractors_and_the_similarity_floor() =>
        Tiers().Line().Should().Be("storage: extractors; retrieval: similarity floor (no gate added); dreaming: off");

    [Fact]
    public void With_the_writer_the_update_judge_and_the_gate_each_leg_says_so() =>
        Tiers(s => { Writer(s); Gate(s, g => { g.UpdateJudge = true; g.Judges.Add(Jev); }); }).Line().Should().Be(
            "storage: writer + update judge (a failed writer call falls back to the extractors); retrieval: judge (jev); dreaming: off");

    [Fact]
    public void A_writer_without_a_judge_and_judge_mode_without_a_judge_say_what_they_lose() =>
        Tiers(s => { Writer(s); Gate(s, _ => { }); }).Line().Should().Be(
            "storage: writer (no update judge, so the writer's closings are not applied; a failed writer call falls back to the extractors); "
            + "retrieval: similarity floor (Judge mode has no judge configured); dreaming: off");

    [Fact]
    public async Task A_judge_left_out_after_repeated_failures_shows_in_retrieval_s_tier()
    {
        var client = new SystemOneClient(new HttpClient(new Down()));
        var options = new MemoryGateOptions { Judges = [Jev], JudgeFailuresBeforeCooldown = 1 };
        var source = new RetrievalTierSource(Options.Create(options), client);
        source.Describe().ToString().Should().Be("retrieval: judge (jev)");

        await FluentActions.Awaiting(() => client.AskAsync(Jev, new { turn = "t" }, new Dictionary<string, YesNo>(), options, default))
            .Should().ThrowAsync<HttpRequestException>();

        source.Describe().ToString().Should().StartWith("retrieval: similarity floor (every judge left out after repeated failures, until ");
    }

    [Fact]
    public async Task The_first_write_logs_the_tiers_once()
    {
        var log = new Lines();
        var stage = new ExtractionStage([], [], [], [], [], Substitute.For<AgentMemory.Abstractions.Services.IEntityResolver>(),
            Options.Create(new ExtractionOptions()), log, writers: null,
            tiers: new MemoryTierReport([new DreamingTierSource()]));
        var message = new Message
        {
            MessageId = "m", ConversationId = "c", SessionId = "s", Role = "user", Content = "We moved to Leoben.", TimestampUtc = DateTimeOffset.UnixEpoch,
        };

        await stage.ExtractAsync([message], ExtractionTypes.All);
        await stage.ExtractAsync([message], ExtractionTypes.All);

        log.Messages.Where(m => m.StartsWith("Memory tiers:", StringComparison.Ordinal)).Should().ContainSingle()
            .Which.Should().Be("Memory tiers: retrieval: similarity floor (no gate added); dreaming: off");
    }

    private sealed class Down : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
    }

    private sealed class Lines : ILogger<ExtractionStage>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
