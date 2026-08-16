using AgentMemory.Abstractions.Repositories;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Isolation;

/// <summary>
/// 25.6. Pins the short-term isolation boundary that the README describes, in both directions.
/// </summary>
/// <remarks>
/// <para>
/// <b>The actual design.</b> <c>:Message</c> nodes carry no <c>owner_id</c>. Short-term messages are
/// addressed <i>by handle</i> — a session id is itself the capability, and reading a session's
/// messages performs no owner check. That is a deliberate decision (recorded as R2, "children
/// by-handle-exempt"), not an oversight, and migrating the schema to add <c>owner_id</c> would be a
/// breaking change to data and to twelve queries for a boundary that already holds where it matters.
/// </para>
/// <para>
/// <b>What this test is for.</b> The README previously claimed owner isolation was "enforced
/// throughout", which overstated it for short-term reads. Documentation and code drifted apart once
/// and would again. So the shape is asserted from the interfaces themselves: reads that take a session
/// handle must NOT take an owner, and the operations that ARE owner-scoped must keep taking one.
/// </para>
/// <para>
/// If someone later adds <c>owner_id</c> to <c>:Message</c>, this test fails — correctly. It is a
/// statement of the current contract, and changing the contract should require changing the statement
/// and the README paragraph that documents it.
/// </para>
/// </remarks>
public sealed class ShortTermByHandleBoundaryTests
{
    [Fact]
    public void SessionScopedMessageReadsTakeNoOwnerAndAreThereforeCapabilityBased()
    {
        // A session id is the capability. If any of these grew an owner parameter, the README's
        // "one deliberate exception" paragraph would be wrong and callers relying on by-handle access
        // would break.
        var byHandle = new[]
        {
            nameof(IMessageRepository.GetRecentBySessionAsync),
            nameof(IMessageRepository.GetAllBySessionAsync),
            nameof(IMessageRepository.GetByConversationAsync),
        };

        foreach (var name in byHandle)
        {
            typeof(IMessageRepository).GetMethods()
                .Where(method => method.Name == name)
                .Should().NotBeEmpty($"{name} must exist");

            typeof(IMessageRepository).GetMethods()
                .Where(method => method.Name == name)
                .SelectMany(method => method.GetParameters())
                .Should().NotContain(
                    parameter => parameter.Name!.Contains("owner", StringComparison.OrdinalIgnoreCase),
                    $"{name} is by-handle by design; an owner parameter here would change the contract "
                    + "the README documents");
        }
    }

    [Fact]
    public void ClearingASessionIsOwnerScopedAndMustStayThatWay()
    {
        // The counterpart, and the reason the exception is narrow rather than general. Reading a
        // session by handle is capability-based; DESTROYING one is owner-scoped, because a leaked
        // session id should never let a caller delete another owner's history. Losing this parameter
        // would be a genuine isolation regression rather than a documented exception.
        // Declared on IMemoryMaintenance, which IMemoryService composes. Reflection does not traverse
        // base interfaces, so the declaring interface is named directly.
        typeof(AgentMemory.Abstractions.Services.IMemoryMaintenance)
            .GetMethod("ClearSessionAsync")!
            .GetParameters()
            .Should().Contain(
                parameter => parameter.Name!.Contains("owner", StringComparison.OrdinalIgnoreCase),
                "clearing a session must remain owner-scoped even though reading one is not");
    }
}
