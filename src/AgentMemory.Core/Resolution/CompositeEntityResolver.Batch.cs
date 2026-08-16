using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;

namespace AgentMemory.Core.Resolution;

internal sealed partial class CompositeEntityResolver
{
    private readonly AsyncLocal<CandidateBatchState?> _candidateBatch = new();

    public IDisposable BeginBatch()
    {
        if (!_options.UseBatchEntityResolutionSnapshots)
            return NoopBatchLease.Instance;
        if (_candidateBatch.Value is not null)
            throw new InvalidOperationException("An entity-resolution batch is already active in this async flow.");

        var state = new CandidateBatchState();
        _candidateBatch.Value = state;
        return new CandidateBatchLease(this, state);
    }

    public async Task PrepareCandidatesAsync(
        IReadOnlyCollection<string> entityTypes,
        MemoryScope? scope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entityTypes);
        var state = _candidateBatch.Value;
        if (state is null || entityTypes.Count == 0)
            return;

        var loads = entityTypes
            .Where(type => !string.IsNullOrWhiteSpace(type))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(type => state.GetOrAddAsync(
                CandidateBatchKey.Create(type, scope),
                () => LoadCandidatesAsync(type, scope, cancellationToken)))
            .ToArray();
        await Task.WhenAll(loads).ConfigureAwait(false);
    }

    public void InvalidateBatch() => _candidateBatch.Value?.Invalidate();

    private async Task<Entity> ResolveAndRememberAsync(
        ExtractedEntity extractedEntity,
        IReadOnlyList<string> sourceMessageIds,
        MemoryScope? scope,
        bool persistResolution,
        CancellationToken cancellationToken)
    {
        var entity = await ResolveEntityCoreAsync(
            extractedEntity,
            sourceMessageIds,
            scope,
            persistResolution,
            cancellationToken).ConfigureAwait(false);
        _candidateBatch.Value?.Remember(
            CandidateBatchKey.Create(extractedEntity.Type, scope), entity);
        return entity;
    }

    private async Task<IReadOnlyList<Entity>> GetBatchCandidatesAsync(
        string type,
        MemoryScope? scope,
        CancellationToken cancellationToken)
    {
        var state = _candidateBatch.Value;
        if (state is null)
            return await LoadCandidatesAsync(type, scope, cancellationToken).ConfigureAwait(false);

        return await state.GetOrAddAsync(
            CandidateBatchKey.Create(type, scope),
            () => LoadCandidatesAsync(type, scope, cancellationToken)).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<Entity>> LoadCandidatesAsync(
        string type,
        MemoryScope? scope,
        CancellationToken cancellationToken)
    {
        // Candidate reads stay owner-scoped. This loads the SAME-TYPE candidates only; the non-strict
        // widening lives in GetCandidatesAsync and is deliberately outside this cache, which is keyed by
        // type. (An earlier note here said type-strict=false was unimplementable "because the repository
        // has no unfiltered GetAll contract" -- true, but the wrong contract to want: the widening is
        // bounded by name, not unbounded over the graph.)
        return await _entityRepository.GetByTypeAsync(type, scope, cancellationToken).ConfigureAwait(false);
    }

    private void EndBatch(CandidateBatchState state)
    {
        if (!ReferenceEquals(_candidateBatch.Value, state))
            return;
        state.Dispose();
        _candidateBatch.Value = null;
    }

    private readonly record struct CandidateBatchKey(
        string Type,
        string? OwnerId,
        bool IncludeShared)
    {
        public static CandidateBatchKey Create(string type, MemoryScope? scope) =>
            new(type.ToUpperInvariant(), scope?.OwnerId, scope?.IncludeShared ?? true);
    }

    private sealed class CandidateBatchState : IDisposable
    {
        private readonly object _gate = new();
        private readonly Dictionary<CandidateBatchKey, Task<List<Entity>>> _snapshots = [];
        private bool _disposed;

        public Task<List<Entity>> GetOrAddAsync(
            CandidateBatchKey key,
            Func<Task<IReadOnlyList<Entity>>> loader)
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_snapshots.TryGetValue(key, out var existing))
                    return existing;
                var created = LoadAsync(loader);
                _snapshots.Add(key, created);
                return created;
            }
        }

        public void Remember(CandidateBatchKey key, Entity entity)
        {
            lock (_gate)
            {
                if (_disposed || !_snapshots.TryGetValue(key, out var snapshot) ||
                    !snapshot.IsCompletedSuccessfully)
                    return;

                var candidates = snapshot.Result;
                var index = candidates.FindIndex(candidate => candidate.EntityId == entity.EntityId);
                if (index >= 0)
                    candidates[index] = entity;
                else
                    candidates.Add(entity);
            }
        }

        public void Invalidate()
        {
            lock (_gate)
                _snapshots.Clear();
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _snapshots.Clear();
            }
        }

        private static async Task<List<Entity>> LoadAsync(Func<Task<IReadOnlyList<Entity>>> loader) =>
            (await loader().ConfigureAwait(false)).ToList();
    }

    private sealed class CandidateBatchLease(
        CompositeEntityResolver owner,
        CandidateBatchState state) : IDisposable
    {
        private CompositeEntityResolver? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.EndBatch(state);
    }

    private sealed class NoopBatchLease : IDisposable
    {
        public static NoopBatchLease Instance { get; } = new();
        public void Dispose() { }
    }
}
