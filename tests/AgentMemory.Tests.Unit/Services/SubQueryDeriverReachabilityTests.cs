using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Core;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Every deriver is reachable from the production entry point (30.10, design step 10).
/// </summary>
/// <remarks>
/// <para>
/// This repository has now found the same defect shape four separate times: a feature built, tested
/// in isolation, and never wired to the path that would run it. The procedural promotion that never
/// fired, the two rerankers registered by nobody, the working-memory tier whose rebuild hook reached
/// only one of two write paths — each passed its own tests.
/// </para>
/// <para>
/// So the guard is reflective rather than enumerated: it discovers every non-abstract
/// <c>ISubQueryDeriver</c> in the assembly and fails if one cannot be reached from
/// <c>AddAgentMemoryCore</c> under some option setting. A third implementation added later and left
/// unregistered fails here rather than shipping inert.
/// </para>
/// </remarks>
public sealed class SubQueryDeriverReachabilityTests
{
    private static ServiceProvider Build(Action<MemoryOptions>? configure = null)
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<IFactRepository>())
            .AddSingleton(Substitute.For<IMessageRepository>());

        services.AddAgentMemoryCore(options => configure?.Invoke(options));
        return services.BuildServiceProvider();
    }

    [Fact]
    public void TheDeterministicDeriverIsWhatTheDefaultResolves()
    {
        using var provider = Build();

        provider.GetRequiredService<ISubQueryDeriver>()
            .DeriverId.Should().Be("det-v1");
    }

    [Fact]
    public void TheLlmDeriverResolvesWhenAskedForAndAChatClientExists()
    {
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<IFactRepository>())
            .AddSingleton(Substitute.For<IMessageRepository>())
            .AddSingleton(Substitute.For<IChatClient>());

        services.AddAgentMemoryCore(options => options.FanOut.UseLlmDerivation = true);
        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<ISubQueryDeriver>()
            .DeriverId.Should().Be("llm-v1");
    }

    [Fact]
    public void AskingForTheLlmDeriverWithNoChatClientFallsBackRatherThanFailingToResolve()
    {
        // An unsatisfiable binding here would take the whole assembler down with it -- the exact break
        // an unconditional registration with a missing dependency caused during the 1.0 lockdown.
        using var provider = Build(options => options.FanOut.UseLlmDerivation = true);

        var resolve = () => provider.GetRequiredService<ISubQueryDeriver>();

        resolve.Should().NotThrow();
        resolve().DeriverId.Should().Be("det-v1");
    }

    [Fact]
    public void EveryDeriverImplementationIsReachableFromTheProductionEntryPoint()
    {
        // Reflective on purpose. An enumerated list would pass forever while a newly added third
        // implementation shipped unregistered and inert -- which is precisely the shape of defect
        // this repository keeps finding.
        var implementations = typeof(DeterministicSubQueryDeriver).Assembly
            .GetTypes()
            .Where(type => typeof(ISubQueryDeriver).IsAssignableFrom(type))
            .Where(type => type is { IsAbstract: false, IsInterface: false })
            .ToList();

        implementations.Should().HaveCountGreaterThanOrEqualTo(2,
            "the deterministic and LLM derivers both exist");

        var reachable = new HashSet<Type>();

        using (var defaultProvider = Build())
        {
            reachable.Add(defaultProvider.GetRequiredService<ISubQueryDeriver>().GetType());
        }

        var withChat = new ServiceCollection()
            .AddLogging()
            .AddSingleton(Substitute.For<IFactRepository>())
            .AddSingleton(Substitute.For<IMessageRepository>())
            .AddSingleton(Substitute.For<IChatClient>());
        withChat.AddAgentMemoryCore(options => options.FanOut.UseLlmDerivation = true);
        using (var llmProvider = withChat.BuildServiceProvider())
        {
            reachable.Add(llmProvider.GetRequiredService<ISubQueryDeriver>().GetType());
        }

        implementations.Should().OnlyContain(
            type => reachable.Contains(type),
            "every deriver must be reachable under SOME option setting, or it ships inert");
    }

    [Fact]
    public void TheAssemblerReceivesTheDeriver()
    {
        // The seam that actually matters: registered-but-not-injected is the same inert outcome as
        // not registered at all, and the DI container reports neither.
        using var provider = Build(options => options.FanOut.Enabled = true);
        using var scope = provider.CreateScope();

        var deriver = scope.ServiceProvider.GetService<ISubQueryDeriver>();

        deriver.Should().NotBeNull(
            "the assembler resolves this by GetService; a null here means the planner can never run");
    }
}
