using AgentMemory.Abstractions.Services;
using AgentMemory.Core;
using AgentMemory.Core.Stubs;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Infrastructure;

/// <summary>
/// #164: the library reads time from <see cref="TimeProvider"/>. A host sets it by registering one; a host that
/// registered its own <see cref="IClock"/>, the 2.0 way, keeps that clock.
/// </summary>
public sealed class TimeProviderRegistrationTests
{
    private static readonly DateTimeOffset At = new(2026, 12, 11, 9, 0, 0, TimeSpan.Zero);

    private static ServiceProvider Build(Action<IServiceCollection> host)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        host(services);
        services.AddAgentMemoryCore(_ => { });
        return services.BuildServiceProvider();
    }

    [Fact]
    public void ByDefaultTheSystemsTimeIsReadAndTheClockReadsIt()
    {
        using var provider = Build(_ => { });

        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
        provider.GetRequiredService<IClock>().Should().BeOfType<SystemClock>()
            .Which.TimeProvider.Should().BeSameAs(TimeProvider.System);
    }

    [Fact]
    public void ARegisteredTimeProviderIsWhatEverythingReads()
    {
        var time = Substitute.For<TimeProvider>();
        time.GetUtcNow().Returns(At);

        using var provider = Build(s => s.AddSingleton(time));

        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(time);
        provider.GetRequiredService<IClock>().UtcNow.Should().Be(At);
    }

    [Fact]
    public void AHostsOwnClockIsStillHonoured()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(At);

        using var provider = Build(s => s.AddSingleton(clock));

        provider.GetRequiredService<IClock>().Should().BeSameAs(clock);
        provider.GetRequiredService<TimeProvider>().GetUtcNow().Should().Be(At);
    }

    [Fact]
    public void AHostsOwnClockRegisteredAfterTheLibraryIsStillHonoured()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(At);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddAgentMemoryCore(_ => { });
        services.AddSingleton(clock);

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<TimeProvider>().GetUtcNow().Should().Be(At);
    }

    [Fact]
    public void TheSamplesSystemClockRegistrationIsTheSystemsTime()
    {
        // The 2.0 samples registered AddSingleton<IClock, SystemClock>(): that is the default, not a host clock, so
        // the TimeProvider is the system's and nothing reads in a circle.
        using var provider = Build(s => s.AddSingleton<IClock, SystemClock>());

        provider.GetRequiredService<TimeProvider>().Should().BeSameAs(TimeProvider.System);
        provider.GetRequiredService<IClock>().UtcNow.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
    }
}
