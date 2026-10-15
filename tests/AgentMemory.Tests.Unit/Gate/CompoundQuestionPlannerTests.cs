using System.Net;
using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Gate;

public sealed class CompoundQuestionPlannerTests
{
    [Fact]
    public async Task Confirmed_compound_uses_the_explicit_detector_and_host_chat_with_original_constraints()
    {
        using var server = new Detector(.9);
        using var http = new HttpClient(server);
        var chat = Substitute.For<IChatClient>();
        ChatMessage[]? messages = null;
        ChatOptions? settings = null;
        chat.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                messages = ci.ArgAt<IEnumerable<ChatMessage>>(0).ToArray();
                settings = ci.ArgAt<ChatOptions?>(1);
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant,
                    "{\"parts\":[\"Who tests the mobile app?\",\"When does the mobile app launch?\"]}")));
            });
        var planner = Planner(http, chat);
        var request = Request();

        var parts = await planner.PlanAsync(request);

        parts.Should().HaveCount(2);
        server.Calls.Should().Be(1);
        server.Url.Should().Be("https://detector.test/v1/systemone");
        server.Body.Should().Contain(request.Question).And.Contain("mobile app").And.NotContain("candidates");
        messages.Should().NotBeNull();
        messages![1].Text.Should().Contain(request.Question).And.Contain("mobile app");
        settings!.MaxOutputTokens.Should().Be(512);
    }

    [Fact]
    public async Task Single_need_does_not_call_the_chat_model()
    {
        using var server = new Detector(.1);
        using var http = new HttpClient(server);
        var chat = Substitute.For<IChatClient>();

        var parts = await Planner(http, chat).PlanAsync(Request());

        server.Calls.Should().Be(1);
        parts.Should().BeEmpty();
        await chat.DidNotReceiveWithAnyArgs().GetResponseAsync(default!, default, default);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_configuration_makes_no_provider_request(bool missingChat)
    {
        using var server = new Detector(.9);
        using var http = new HttpClient(server);
        var options = new MemoryGateOptions { CompoundDetector = missingChat ? Endpoint() : null };
        var planner = new SystemOneCompoundQuestionPlanner(new SystemOneClient(http),
            missingChat ? null : Substitute.For<IChatClient>(), Options.Create(options));

        var action = () => planner.PlanAsync(Request());

        await action.Should().ThrowAsync<InvalidOperationException>();
        server.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"parts\":[\"one\"]}")]
    [InlineData("{\"parts\":[\"one\",\" ONE \"]}")]
    [InlineData("{\"parts\":[\"one\",\"two\",\"three\",\"four\"]}")]
    [InlineData("{\"parts\":[1,2]}")]
    [InlineData("{\"parts\":null}")]
    [InlineData("{\"parts\":[],\"parts\":[\"one\",\"two\"]}")]
    public void Invalid_model_output_is_not_a_plan(string json)
    {
        var action = () => SystemOneCompoundQuestionPlanner.Parse(json);
        action.Should().Throw<FormatException>();
    }

    [Fact]
    public void Valid_empty_and_bounded_plans_parse_without_answer_inference()
    {
        SystemOneCompoundQuestionPlanner.Parse("{\"parts\":[]}").Should().BeEmpty();
        SystemOneCompoundQuestionPlanner.Parse("{\"parts\":[\" first? \",\"second?\"]}").Should().Equal("first?", "second?");
    }

    [Fact]
    public void Off_does_not_resolve_any_planner_or_chat_dependency()
    {
        var services = Services();
        services.AddSingleton<ICompoundQuestionPlanner>(_ => throw new InvalidOperationException("planner resolved while off"));
        services.AddSingleton<IChatClient>(_ => throw new InvalidOperationException("chat resolved while off"));
        services.AddAgentMemoryGate(o => o.CompoundQuestions = false);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMemoryContextAssembler>().Should().BeOfType<GatedMemoryContextAssembler>();
    }

    [Fact]
    public async Task Off_preserves_the_actual_System_One_request_bytes()
    {
        using var server = new Detector(.9);
        using var http = new HttpClient(server);
        var options = Options.Create(new MemoryGateOptions { Judges = [Endpoint()], CompoundQuestions = false });
        var judge = new SystemOneMemoryGate(new SystemOneClient(http), options, NullLogger<SystemOneMemoryGate>.Instance);
        var inner = Substitute.For<IMemoryContextAssembler>();
        inner.AssembleContextAsync(Arg.Any<RecallRequest>(), Arg.Any<CancellationToken>()).Returns(new MemoryContext
        {
            SessionId = "s", AssembledAtUtc = DateTimeOffset.UnixEpoch,
            RelevantFacts = new MemoryContextSection<Fact>
            {
                Items = [new Fact { FactId = "one", Subject = "Orion", Predicate = "has", Object = "a launch",
                    Confidence = 1, CreatedAtUtc = DateTimeOffset.UnixEpoch }],
            },
        });
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UnixEpoch);
        var planner = Substitute.For<ICompoundQuestionPlanner>();
        var oldConstructor = new GatedMemoryContextAssembler(inner, judge, options,
            NullLogger<GatedMemoryContextAssembler>.Instance, null, clock);
        var newConstructor = new GatedMemoryContextAssembler(inner, judge, options,
            NullLogger<GatedMemoryContextAssembler>.Instance, null, clock, planner);
        var request = new RecallRequest { Query = "Who tests Orion and when is launch?", SessionId = "s" };

        var before = await oldConstructor.AssembleContextAsync(request);
        var originalBytes = server.Body;
        var after = await newConstructor.AssembleContextAsync(request);

        server.Calls.Should().Be(2);
        server.Body.Should().Be(originalBytes);
        after.RelevantFacts.Should().BeEquivalentTo(before.RelevantFacts);
        after.Metadata.Keys.Should().BeEquivalentTo(before.Metadata.Keys);
        await planner.DidNotReceiveWithAnyArgs().PlanAsync(default!, default);
    }

    [Fact]
    public void On_uses_a_registered_custom_planner_without_resolving_chat()
    {
        var services = Services();
        var resolved = false;
        services.AddSingleton<ICompoundQuestionPlanner>(_ => { resolved = true; return Substitute.For<ICompoundQuestionPlanner>(); });
        services.AddSingleton<IChatClient>(_ => throw new InvalidOperationException("custom planner does not need chat"));
        services.AddAgentMemoryGate(o => o.CompoundQuestions = true);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<IMemoryContextAssembler>();

        resolved.Should().BeTrue();
    }

    private static ServiceCollection Services()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<IMemoryContextAssembler>());
        services.AddSingleton(Substitute.For<IMemoryGate>());
        return services;
    }

    private static CompoundQuestionRequest Request() => new()
    {
        Question = "Who tests it and when is launch?", NowUtc = DateTimeOffset.UnixEpoch,
        Conversation = [new MemoryGateTurn("user", "We are discussing the mobile app, not the website.")],
    };
    private static SystemOneEndpoint Endpoint() => new() { Endpoint = new Uri("https://detector.test/v1/systemone"), Name = "detector" };
    private static SystemOneCompoundQuestionPlanner Planner(HttpClient http, IChatClient chat) => new(
        new SystemOneClient(http), chat, Options.Create(new MemoryGateOptions { CompoundDetector = Endpoint() }));

    private sealed class Detector(double score) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public string? Url { get; private set; }
        public string? Body { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Url = request.RequestUri!.ToString();
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            using var json = JsonDocument.Parse(Body);
            var answers = json.RootElement.GetProperty("questions").EnumerateObject()
                .ToDictionary(p => p.Name, _ => new { noul = score });
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(new { answers })),
            };
        }
    }
}
