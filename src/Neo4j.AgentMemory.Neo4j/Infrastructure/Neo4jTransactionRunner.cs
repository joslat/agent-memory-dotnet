using Microsoft.Extensions.Logging;
using Neo4j.Driver;

namespace Neo4j.AgentMemory.Neo4j.Infrastructure;

public sealed class Neo4jTransactionRunner : INeo4jTransactionRunner
{
    private readonly INeo4jSessionFactory _sessionFactory;
    private readonly ILogger<Neo4jTransactionRunner> _logger;

    public Neo4jTransactionRunner(INeo4jSessionFactory sessionFactory, ILogger<Neo4jTransactionRunner> logger)
    {
        _sessionFactory = sessionFactory;
        _logger = logger;
    }

    public async Task<T> ReadAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
    {
        await using var session = _sessionFactory.OpenSession(AccessMode.Read);
        try
        {
            return await session.ExecuteReadAsync(work);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing read transaction.");
            throw;
        }
    }

    public async Task ReadAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
    {
        await ReadAsync(async tx =>
        {
            await work(tx);
            return true;
        }, cancellationToken);
    }

    public async Task<T> WriteAsync<T>(Func<IAsyncQueryRunner, Task<T>> work, CancellationToken cancellationToken = default)
    {
        await using var session = _sessionFactory.OpenSession(AccessMode.Write);
        try
        {
            return await session.ExecuteWriteAsync(work);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error executing write transaction.");
            throw;
        }
    }

    public async Task WriteAsync(Func<IAsyncQueryRunner, Task> work, CancellationToken cancellationToken = default)
    {
        await WriteAsync(async tx =>
        {
            await work(tx);
            return true;
        }, cancellationToken);
    }
}
