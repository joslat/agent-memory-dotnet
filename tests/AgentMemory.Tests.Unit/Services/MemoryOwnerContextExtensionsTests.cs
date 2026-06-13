using FluentAssertions;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Validates the host-facing owner-scope pattern (the proper closure for the cycle-3 finding #4 isolation
/// gap). The contrast with the broken provider-set-inside-an-awaited-hook approach is the whole point: a
/// value set in an enclosing scope DOES flow down (via <see cref="System.Threading.AsyncLocal{T}"/>) into the
/// asynchronous work awaited inside it — which is exactly what an agent run + its tool calls are.
/// </summary>
public sealed class MemoryOwnerContextExtensionsTests
{
    // Simulates a facade tool reading the ambient owner mid-run — after an await, in nested async work.
    private static async Task<string?> ReadOwnerLikeAToolCall(IMemoryOwnerContext ctx)
    {
        await Task.Yield();
        return ctx.UserId;
    }

    [Fact]
    public async Task BeginOwnerScope_FlowsOwnerIntoAwaitedNestedWork()
    {
        var ctx = new DefaultMemoryOwnerContext();

        string? observed;
        using (ctx.BeginOwnerScope("alice"))
        {
            // The owner must be visible to async work awaited INSIDE the scope (the run + its tool calls).
            observed = await ReadOwnerLikeAToolCall(ctx);
        }

        observed.Should().Be("alice");
    }

    [Fact]
    public async Task BeginOwnerScope_RestoresPreviousValue_OnDispose()
    {
        var ctx = new DefaultMemoryOwnerContext();

        using (ctx.BeginOwnerScope("alice"))
        {
            ctx.UserId.Should().Be("alice");
            await Task.Yield();
        }

        ctx.UserId.Should().BeNull("the scope restores the previous (shared/global) owner on dispose");
    }

    [Fact]
    public async Task BeginOwnerScope_Nested_RestoresOuterOwner()
    {
        var ctx = new DefaultMemoryOwnerContext();

        using (ctx.BeginOwnerScope("alice"))
        {
            ctx.UserId.Should().Be("alice");
            using (ctx.BeginOwnerScope("bob"))
            {
                (await ReadOwnerLikeAToolCall(ctx)).Should().Be("bob");
            }
            ctx.UserId.Should().Be("alice", "disposing the inner scope restores the outer owner");
        }

        ctx.UserId.Should().BeNull();
    }

    [Fact]
    public async Task BeginOwnerScope_NullUserId_ScopesToShared()
    {
        var ctx = new DefaultMemoryOwnerContext { UserId = "alice" };

        using (ctx.BeginOwnerScope(null))
        {
            (await ReadOwnerLikeAToolCall(ctx)).Should().BeNull("a null owner scopes the work to shared/global");
        }

        ctx.UserId.Should().Be("alice");
    }

    [Fact]
    public void BeginOwnerScope_NullContext_Throws()
    {
        IWritableMemoryOwnerContext ctx = null!;
        FluentActions.Invoking(() => ctx.BeginOwnerScope("x"))
            .Should().Throw<ArgumentNullException>();
    }
}
