namespace AgentMemory.Abstractions.Services;

/// <summary>
/// Helpers for establishing the ambient memory store (R1b) around a unit of work, mirroring
/// <see cref="MemoryOwnerContextExtensions.BeginOwnerScope"/> for <see cref="IWritableMemoryStoreContext"/>.
/// </summary>
public static class MemoryStoreContextExtensions
{
    /// <summary>
    /// Sets <see cref="IWritableMemoryStoreContext.ApplicationId"/> to <paramref name="applicationId"/>
    /// for the current async flow and restores the previous value when the returned scope is disposed.
    /// Because the store context is typically <see cref="System.Threading.AsyncLocal{T}"/>-backed, the
    /// value flows into any asynchronous work awaited inside the <c>using</c> block. A <c>null</c>
    /// <paramref name="applicationId"/> scopes the work to the default store.
    /// </summary>
    public static IDisposable BeginStoreScope(this IWritableMemoryStoreContext context, string? applicationId)
    {
        ArgumentNullException.ThrowIfNull(context);
        var previous = context.ApplicationId;
        context.ApplicationId = applicationId;
        return new StoreScope(context, previous);
    }

    private sealed class StoreScope : IDisposable
    {
        private readonly IWritableMemoryStoreContext _context;
        private readonly string? _previous;
        private bool _disposed;

        public StoreScope(IWritableMemoryStoreContext context, string? previous)
        {
            _context = context;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _context.ApplicationId = _previous;
        }
    }
}
