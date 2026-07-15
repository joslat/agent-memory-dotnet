using FluentAssertions;
using AgentMemory.Abstractions.Services;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// Mirrors <see cref="MemoryOwnerContextExtensionsTests"/> for <see cref="MemoryStoreContextExtensions.BeginStoreScope"/>.
/// </summary>
public sealed class MemoryStoreContextExtensionsTests
{
    private sealed class TestStoreContext : IWritableMemoryStoreContext
    {
        public string? ApplicationId { get; set; }
    }

    private static async Task<string?> ReadApplicationIdLikeAToolCall(IMemoryStoreContext ctx)
    {
        await Task.Yield();
        return ctx.ApplicationId;
    }

    [Fact]
    public async Task BeginStoreScope_FlowsApplicationIdIntoAwaitedNestedWork()
    {
        var ctx = new TestStoreContext();

        string? observed;
        using (ctx.BeginStoreScope("app-1"))
        {
            observed = await ReadApplicationIdLikeAToolCall(ctx);
        }

        observed.Should().Be("app-1");
    }

    [Fact]
    public async Task BeginStoreScope_RestoresPreviousValue_OnDispose()
    {
        var ctx = new TestStoreContext();

        using (ctx.BeginStoreScope("app-1"))
        {
            ctx.ApplicationId.Should().Be("app-1");
            await Task.Yield();
        }

        ctx.ApplicationId.Should().BeNull("the scope restores the previous (default-store) application on dispose");
    }

    [Fact]
    public async Task BeginStoreScope_Nested_RestoresOuterApplicationId()
    {
        var ctx = new TestStoreContext();

        using (ctx.BeginStoreScope("app-1"))
        {
            ctx.ApplicationId.Should().Be("app-1");
            using (ctx.BeginStoreScope("app-2"))
            {
                (await ReadApplicationIdLikeAToolCall(ctx)).Should().Be("app-2");
            }
            ctx.ApplicationId.Should().Be("app-1", "disposing the inner scope restores the outer application id");
        }

        ctx.ApplicationId.Should().BeNull();
    }

    [Fact]
    public async Task BeginStoreScope_NullApplicationId_ScopesToDefaultStore()
    {
        var ctx = new TestStoreContext { ApplicationId = "app-1" };

        using (ctx.BeginStoreScope(null))
        {
            (await ReadApplicationIdLikeAToolCall(ctx)).Should().BeNull("a null application id scopes the work to the default store");
        }

        ctx.ApplicationId.Should().Be("app-1");
    }

    [Fact]
    public void BeginStoreScope_NullContext_Throws()
    {
        IWritableMemoryStoreContext ctx = null!;
        FluentActions.Invoking(() => ctx.BeginStoreScope("x"))
            .Should().Throw<ArgumentNullException>();
    }
}
