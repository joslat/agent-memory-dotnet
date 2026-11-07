namespace AgentMemory.Abstractions.Services;

/// <summary>
/// The current time, as AgentMemory 1.x and 2.0 read it.
/// </summary>
/// <remarks>
/// Since 2.1 the library reads time from <see cref="TimeProvider"/> (#164): register one (for example
/// <c>FakeTimeProvider</c> in tests) and every component follows it. <see cref="IClock"/> remains for the public
/// constructors that take it and for hosts that registered their own; such a registration is still honoured, because
/// the default <see cref="TimeProvider"/> then reads it. The public constructors switch to <see cref="TimeProvider"/> in
/// 3.0, when this interface is removed.
/// </remarks>
public interface IClock
{
    /// <summary>
    /// Gets the current UTC time.
    /// </summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>An <see cref="IClock"/> that reads a <see cref="System.TimeProvider"/>.</summary>
public class TimeProviderClock : IClock
{
    /// <summary>A clock reading <paramref name="timeProvider"/>.</summary>
    public TimeProviderClock(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        TimeProvider = timeProvider;
    }

    /// <summary>The time provider this clock reads.</summary>
    public TimeProvider TimeProvider { get; }

    /// <inheritdoc/>
    public DateTimeOffset UtcNow => TimeProvider.GetUtcNow();
}

/// <summary>Bridges an <see cref="IClock"/> to <see cref="TimeProvider"/>.</summary>
public static class ClockTimeProviderExtensions
{
    /// <summary>
    /// The <see cref="TimeProvider"/> this clock reads: the one behind a <see cref="TimeProviderClock"/>, else a provider
    /// whose <see cref="TimeProvider.GetUtcNow"/> reads <paramref name="clock"/>.
    /// </summary>
    public static TimeProvider ToTimeProvider(this IClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        return clock is TimeProviderClock backed ? backed.TimeProvider : new ClockTimeProvider(clock);
    }

    private sealed class ClockTimeProvider(IClock clock) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => clock.UtcNow;
    }
}
