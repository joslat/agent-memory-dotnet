using FluentAssertions;
using Microsoft.Agents.AI;
using AgentMemory.AgentFramework;

namespace AgentMemory.Tests.Unit.AgentFramework;

/// <summary>
/// 30.5. The checkpoint token: written by the host or the provider, read back across a session's
/// serialize/restore seam.
/// </summary>
/// <remarks>
/// <para>
/// The token is deliberately <b>not</b> a stored node. A <c>:MemoryCheckpoint</c> label would be a real
/// schema-parity cost for a need no host has yet, and the state bag already persists with the session
/// for free. That choice is what these tests are protecting: the value has to survive a string
/// round-trip, and it has to fail <i>closed</i> — to "no checkpoint", meaning full recall — rather than
/// guessing a window when the stored text is unreadable.
/// </para>
/// </remarks>
public sealed class DeltaCheckpointStateBagTests
{
    private sealed class TestAgentSession : AgentSession;

    private static AgentSession NewSession() => new TestAgentSession();

    [Fact]
    public void ACheckpointRoundTripsThroughTheStateBag()
    {
        var session = NewSession();
        var checkpoint = new DateTimeOffset(2026, 8, 1, 9, 14, 30, TimeSpan.Zero);

        session.SetDeltaCheckpoint(checkpoint);

        session.GetDeltaCheckpoint().Should().Be(checkpoint);
    }

    [Fact]
    public void ACheckpointWrittenInALocalOffsetComesBackAsTheSameInstant()
    {
        // Stored normalised to UTC: two hosts in different time zones must agree on the window, and a
        // window that shifts by an offset silently re-reports or skips whole hours of change.
        var session = NewSession();
        var local = new DateTimeOffset(2026, 8, 1, 11, 14, 30, TimeSpan.FromHours(2));

        session.SetDeltaCheckpoint(local);

        session.GetDeltaCheckpoint().Should().Be(local.ToUniversalTime());
    }

    [Fact]
    public void SubSecondPrecisionSurvivesTheRoundTrip()
    {
        // The window is half-open on both ends. A checkpoint rounded to the second would put every write
        // inside that second on the wrong side of the boundary.
        var session = NewSession();
        var checkpoint = new DateTimeOffset(2026, 8, 1, 9, 14, 30, 123, TimeSpan.Zero).AddTicks(4567);

        session.SetDeltaCheckpoint(checkpoint);

        session.GetDeltaCheckpoint().Should().Be(checkpoint);
    }

    [Fact]
    public void ASessionThatNeverAcknowledgedAnythingHasNoCheckpoint()
    {
        NewSession().GetDeltaCheckpoint().Should().BeNull();
    }

    [Fact]
    public void ANullSessionHasNoCheckpointRatherThanThrowing()
    {
        AgentSession? session = null;

        session.GetDeltaCheckpoint().Should().BeNull();
    }

    [Fact]
    public void UnparseableStoredTextReadsAsNoCheckpointRatherThanAGuessedWindow()
    {
        var session = NewSession();
        session.StateBag.SetValue(
            new AgentFrameworkOptions().DefaultDeltaCheckpointKey, "not a date",
            System.Text.Json.JsonSerializerOptions.Default);

        session.GetDeltaCheckpoint().Should().BeNull(
            "full recall is the safe degradation; a guessed window would be asserted as fact");
    }

    [Fact]
    public void ACustomKeyIsHonouredOnBothSides()
    {
        var options = new AgentFrameworkOptions { DefaultDeltaCheckpointKey = "my_checkpoint" };
        var session = NewSession();
        var checkpoint = new DateTimeOffset(2026, 8, 1, 9, 14, 30, TimeSpan.Zero);

        session.SetDeltaCheckpoint(checkpoint, options);

        session.GetDeltaCheckpoint(options).Should().Be(checkpoint);
        session.GetDeltaCheckpoint().Should().BeNull("the default key was never written");
    }
}
