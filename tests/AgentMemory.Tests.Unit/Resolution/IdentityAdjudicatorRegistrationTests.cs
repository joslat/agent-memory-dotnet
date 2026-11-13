using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NSubstitute;
using System.Reflection;
using System.Runtime.ExceptionServices;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityAdjudicatorRegistrationTests
{
    private static IServiceCollection Register(IServiceCollection? services, Func<IServiceProvider, IChatClient>? factory, int maximum = 8)
    {
        var method = typeof(AgentMemory.Extraction.Llm.ServiceCollectionExtensions).GetMethod("AddIdentityAdjudication",
            BindingFlags.Public | BindingFlags.Static, [typeof(IServiceCollection), typeof(Func<IServiceProvider, IChatClient>), typeof(int)]);
        method.Should().NotBeNull("the dedicated optional adjudicator needs an explicit public DI seam");
        try { return (IServiceCollection)method!.Invoke(null, [services, factory, maximum])!; }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
    }

    [Fact]
    public async Task Dedicated_client_factory_is_lazy_and_one_adapter_is_shared_across_scopes()
    {
        var calls = 0; var client = Substitute.For<IChatClient>(); var services = new ServiceCollection();
        Register(services, _ => { calls++; return client; }); calls.Should().Be(0);
        await using var provider = services.BuildServiceProvider();
        using var first = provider.CreateScope(); using var second = provider.CreateScope();
        first.ServiceProvider.GetRequiredService<IIdentityAdjudicator>().Should().BeSameAs(second.ServiceProvider.GetRequiredService<IIdentityAdjudicator>());
        calls.Should().Be(1); client.ReceivedCalls().Should().BeEmpty();
    }

    [Fact]
    public async Task Existing_application_adjudicator_wins_without_creating_another_client()
    {
        var custom = Substitute.For<IIdentityAdjudicator>(); var services = new ServiceCollection();
        services.AddSingleton(custom);
        Register(services, _ => throw new InvalidOperationException("must not run"));
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IIdentityAdjudicator>().Should().BeSameAs(custom);
    }

    [Theory]
    [InlineData(-1)] [InlineData(0)] [InlineData(65)]
    public void Invalid_inflight_bounds_are_rejected_before_registration(int maximum)
    {
        var services = new ServiceCollection(); var before = services.Count;
        Action add = () => Register(services, _ => Substitute.For<IChatClient>(), maximum);
        add.Should().Throw<ArgumentOutOfRangeException>(); services.Count.Should().Be(before);
    }

    [Fact]
    public void Null_collection_is_rejected() =>
        FluentActions.Invoking(() => Register(null, _ => Substitute.For<IChatClient>())).Should().Throw<ArgumentNullException>();

    [Fact]
    public void Null_factory_is_rejected_without_registration()
    {
        var services = new ServiceCollection(); var before = services.Count;
        FluentActions.Invoking(() => Register(services, null)).Should().Throw<ArgumentNullException>();
        services.Count.Should().Be(before);
    }

    [Fact]
    public async Task Adapter_uses_only_the_explicit_model_and_does_not_dispose_the_host_client()
    {
        var dedicated = Substitute.For<IChatClient>(); var extraction = Substitute.For<IChatClient>();
        dedicated.GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>())
            .Returns(new ChatResponse(new ChatMessage(ChatRole.Assistant, "{}")));
        var services = new ServiceCollection(); services.AddSingleton(extraction);
        Register(services, _ => dedicated);
        var provider = services.BuildServiceProvider();
        await provider.GetRequiredService<IIdentityAdjudicator>().GetDecisionAsync(new()
        { SystemInstructions = "policy", DataJson = "{}", MaxOutputTokens = 512 });
        await provider.DisposeAsync();
        await dedicated.Received(1).GetResponseAsync(Arg.Any<IEnumerable<ChatMessage>>(), Arg.Any<ChatOptions?>(), Arg.Any<CancellationToken>());
        extraction.ReceivedCalls().Should().BeEmpty(); dedicated.DidNotReceive().Dispose();
    }

    [Fact]
    public async Task Registration_does_not_enable_identity_or_resolve_its_model()
    {
        var services = new ServiceCollection(); services.AddOptions<IdentityResolutionOptions>();
        Register(services, _ => throw new InvalidOperationException("must stay lazy"));
        await using var provider = services.BuildServiceProvider();
        provider.GetRequiredService<IOptions<IdentityResolutionOptions>>().Value.Enabled.Should().BeNull("registration never switches identity on or off");
    }

    [Fact]
    public async Task Null_factory_result_fails_when_the_service_is_first_resolved()
    {
        var services = new ServiceCollection(); Register(services, _ => null!);
        await using var provider = services.BuildServiceProvider();
        FluentActions.Invoking(() => provider.GetRequiredService<IIdentityAdjudicator>()).Should().Throw<InvalidOperationException>();
    }
}
