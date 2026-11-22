using Neo4j.Driver;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// DRT-6 (C-01): a runner that can send a read of ONE statement without a transaction of its own (an auto-commit query:
/// 2 round trips instead of begin, the statement, commit and reset's 4), with a managed read's retries. Inside a write
/// unit the read runs on the unit's transaction, as every read does. A separate interface, not a member of
/// <see cref="INeo4jTransactionRunner"/>: a runner that does not implement it (a test double, a decorator) gets its own
/// <see cref="INeo4jTransactionRunner.ReadAsync{T}(Func{IAsyncQueryRunner, Task{T}}, CancellationToken)"/>, unchanged.
/// A decorator of the runner implements it too and forwards to the runner it wraps when that one does, as with
/// <see cref="INeo4jAtomicTransactionRunner"/>. <see cref="Neo4jOptions.SingleStatementReads"/> turns the path off.
/// </summary>
public interface INeo4jSingleStatementReader
{
    /// <summary>The work must send one statement and read its result before it returns.</summary>
    Task<T> ReadSingleAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default);
}

/// <summary>DRT-6: how a call site asks for a single-statement read.</summary>
internal static class Neo4jSingleStatementReads
{
    /// <summary>
    /// A read whose work sends one statement: the single-statement path when the runner has one, else its ordinary read.
    /// </summary>
    public static Task<T> ReadSingleAsync<T>(
        this INeo4jTransactionRunner runner, Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default) =>
        runner is INeo4jSingleStatementReader single
            ? single.ReadSingleAsync(work, cancellationToken)
            : runner.ReadAsync(work, cancellationToken);
}
