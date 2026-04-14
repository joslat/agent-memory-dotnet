using Neo4j.Driver;

namespace Neo4j.AgentMemory.Tests.Unit.TestHelpers;

/// <summary>
/// A minimal IResultCursor that yields a fixed list of records.
/// Works with Neo4j.Driver extension methods like SingleAsync and ToListAsync
/// which rely on IAsyncEnumerable iteration.
/// </summary>
internal sealed class FakeResultCursor : IResultCursor
{
    private readonly IReadOnlyList<IRecord> _records;
    private int _index = -1;

    public FakeResultCursor(params IRecord[] records)
    {
        _records = records;
    }

    public IRecord Current => _index >= 0 && _index < _records.Count ? _records[_index] : null!;

    public Task<string[]> KeysAsync() => Task.FromResult(Array.Empty<string>());

    public Task<IResultSummary> ConsumeAsync() => Task.FromResult((IResultSummary)null!);

    public Task<IRecord> PeekAsync() =>
        _index + 1 < _records.Count
            ? Task.FromResult(_records[_index + 1])
            : Task.FromException<IRecord>(new InvalidOperationException("No more records"));

    public Task<bool> FetchAsync()
    {
        _index++;
        return Task.FromResult(_index < _records.Count);
    }

    public IAsyncEnumerator<IRecord> GetAsyncEnumerator(CancellationToken cancellationToken = default)
        => new RecordEnumerator(_records, cancellationToken);

    public bool IsOpen => true;

    private sealed class RecordEnumerator : IAsyncEnumerator<IRecord>
    {
        private readonly IReadOnlyList<IRecord> _records;
        private int _index = -1;

        public RecordEnumerator(IReadOnlyList<IRecord> records, CancellationToken cancellationToken)
        {
            _records = records;
        }

        public IRecord Current => _records[_index];

        public ValueTask<bool> MoveNextAsync()
        {
            _index++;
            return new ValueTask<bool>(_index < _records.Count);
        }

        public ValueTask DisposeAsync() => default;
    }
}
