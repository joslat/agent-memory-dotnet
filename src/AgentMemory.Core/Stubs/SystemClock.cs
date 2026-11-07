using AgentMemory.Abstractions.Services;

namespace AgentMemory.Core.Stubs;

/// <summary>
/// The default <see cref="IClock"/>: reads the registered <see cref="TimeProvider"/>, else the system's.
/// </summary>
public sealed class SystemClock : TimeProviderClock
{
    /// <summary>A clock reading <see cref="TimeProvider.System"/>.</summary>
    public SystemClock() : base(System.TimeProvider.System) { }

    /// <summary>A clock reading <paramref name="timeProvider"/>.</summary>
    public SystemClock(TimeProvider timeProvider) : base(timeProvider) { }
}
