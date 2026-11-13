namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>Callback-owned mutexes shared by repositories using the same transaction runner.</summary>
internal static class IdentityWriteScope
{
    private static readonly AsyncLocal<Lease?> Current = new();

    public static IDisposable Enter(INeo4jTransactionRunner runner, IReadOnlyList<string> scopes)
    {
        if (Current.Value is not null) throw new InvalidOperationException("An identity write scope is already active.");
        var lease = new Lease(runner, scopes); Current.Value = lease; return lease;
    }

    public static bool Reuse(INeo4jTransactionRunner runner, IEnumerable<string> scopes)
    {
        if (Current.Value is not { } lease) return false;
        if (!lease.IsActive || !ReferenceEquals(lease.Runner, runner) || scopes.Any(scope => !lease.Scopes.Contains(scope)))
            throw new InvalidOperationException("Identity storage cannot outlive, widen or leave its owned write scope.");
        return true;
    }

    private sealed class Lease(INeo4jTransactionRunner runner, IReadOnlyList<string> scopes) : IDisposable
    {
        private int _active = 1;
        public INeo4jTransactionRunner Runner { get; } = runner;
        public HashSet<string> Scopes { get; } = scopes.ToHashSet(StringComparer.Ordinal);
        public bool IsActive => Volatile.Read(ref _active) != 0;
        public void Dispose()
        {
            Interlocked.Exchange(ref _active, 0);
            Current.Value = null;
        }
    }
}
