namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Helpers for establishing the ambient memory owner (IC8) around a unit of work — typically an agent
/// run that may invoke the LLM-invokable facade tools (<c>search_memory</c> / <c>remember_*</c>), which
/// scope <em>only</em> by <see cref="IMemoryOwnerContext"/>.
/// </summary>
/// <remarks>
/// <para>
/// Why a host-level scope is needed: the default owner context is <see cref="System.Threading.AsyncLocal{T}"/>
/// -backed. An <see cref="System.Threading.AsyncLocal{T}"/> value flows <em>down</em> from a caller into the
/// async work it awaits, but a value set <em>inside</em> an already-awaited method does not propagate back to
/// its caller. The MAF context/chat-history providers therefore cannot, on their own, make a value reach the
/// tool calls that the framework runs <em>after</em> the provider's pre-run hook returns. The reliable pattern
/// is to set the owner in code that <em>encloses</em> the run:
/// </para>
/// <code>
/// using (ownerContext.BeginOwnerScope(userId))
/// {
///     await agent.RunAsync(message, session); // RunAsync + its tool calls inherit the owner
/// }
/// </code>
/// <para>
/// Alternatively, register a <em>scoped</em> <see cref="IWritableMemoryOwnerContext"/> and run each turn in
/// its own DI scope, so the provider and the tools resolve the same per-run instance.
/// </para>
/// </remarks>
public static class MemoryOwnerContextExtensions
{
    /// <summary>
    /// Sets <see cref="IWritableMemoryOwnerContext.UserId"/> to <paramref name="userId"/> for the current
    /// async flow and restores the previous value when the returned scope is disposed. Because the owner
    /// context is <see cref="System.Threading.AsyncLocal{T}"/>-backed, the value flows into any asynchronous
    /// work awaited inside the <c>using</c> block (e.g. an agent run and the tools it invokes). A
    /// <c>null</c> <paramref name="userId"/> scopes the work to shared/global memory.
    /// </summary>
    public static IDisposable BeginOwnerScope(this IWritableMemoryOwnerContext context, string? userId)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = context.UserId;
        context.UserId = userId;
        return new OwnerScope(context, previous);
    }

    private sealed class OwnerScope : IDisposable
    {
        private readonly IWritableMemoryOwnerContext _context;
        private readonly string? _previous;
        private bool _disposed;

        public OwnerScope(IWritableMemoryOwnerContext context, string? previous)
        {
            _context = context;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _context.UserId = _previous;
        }
    }
}
