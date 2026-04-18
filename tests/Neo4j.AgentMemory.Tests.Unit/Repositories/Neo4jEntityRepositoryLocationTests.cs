using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Neo4j.AgentMemory.Abstractions.Domain;
using Neo4j.AgentMemory.Neo4j.Infrastructure;
using Neo4j.AgentMemory.Neo4j.Repositories;
using Neo4j.Driver;
using NSubstitute;

namespace Neo4j.AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// Unit tests for geospatial entity persistence and spatial query methods.
/// </summary>
public sealed class Neo4jEntityRepositoryLocationTests
{
    // ── Helpers ──

    private static INode BuildNodeStub(string id = "e-1", string name = "Test", string type = "LOCATION")
    {
        var node = Substitute.For<INode>();
        var props = new Dictionary<string, object>
        {
            ["id"]         = id,
            ["name"]       = name,
            ["type"]       = type,
            ["confidence"] = 0.9,
            ["created_at"] = DateTimeOffset.UtcNow.ToString("O"),
            ["aliases"]    = new List<object>(),
            ["source_message_ids"] = new List<object>()
        };
        node.Properties.Returns(props);
        node[Arg.Any<string>()].Returns(ci => props[ci.Arg<string>()]);
        return node;
    }

    private static IResultCursor BuildFirstCallCursor()
    {
        var node   = BuildNodeStub();
        var record = Substitute.For<IRecord>();
        record["e"].Returns(node);

        var cursor = Substitute.For<IResultCursor>();
        cursor.Current.Returns(record);

        // FetchAsync: true on first call, false on all subsequent calls
        int fetchCount = 0;
        cursor.FetchAsync().Returns(_ => Task.FromResult(++fetchCount == 1));

        return cursor;
    }

    /// <summary>
    /// Creates a capture for WriteAsync&lt;Entity&gt; used by UpsertAsync.
    /// The first RunAsync call (main MERGE) returns a stub IRecord/INode;
    /// subsequent calls (SET embedding, SET location) return empty cursors.
    /// </summary>
    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateEntityUpsertCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task<Entity>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<Entity>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));

                        // First call is the main MERGE — return a cursor that SingleAsync can consume.
                        if (calls.Count == 1)
                            return Task.FromResult(BuildFirstCallCursor());

                        // All other calls (SET embedding, SET location, etc.) return empty cursor.
                        var empty = Substitute.For<IResultCursor>();
                        int c = 0;
                        empty.FetchAsync().Returns(_ => Task.FromResult(++c == 0));  // always false
                        return Task.FromResult(empty);
                    });
                return await work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateWriteCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .WriteAsync(Arg.Any<Func<IAsyncQueryRunner, Task>>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(Substitute.For<IResultCursor>());
                    });
                return work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreateEntityListReadCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Entity>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<List<Entity>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                var cursor = Substitute.For<IResultCursor>();
                cursor.FetchAsync().Returns(Task.FromResult(false));
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(cursor);
                    });
                return await work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    private static (Neo4jEntityRepository Repo, List<(string Cypher, object? Parameters)> Calls)
        CreatePagedEntityReadCapture()
    {
        var calls = new List<(string Cypher, object? Parameters)>();
        var txRunner = Substitute.For<INeo4jTransactionRunner>();
        txRunner
            .ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<PagedResult<Entity>>>>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                var work = call.Arg<Func<IAsyncQueryRunner, Task<PagedResult<Entity>>>>();
                var runner = Substitute.For<IAsyncQueryRunner>();
                var cursor = Substitute.For<IResultCursor>();
                cursor.FetchAsync().Returns(Task.FromResult(false));
                runner
                    .RunAsync(Arg.Any<string>(), Arg.Any<object>())
                    .Returns(ci =>
                    {
                        calls.Add((ci.Arg<string>(), ci.ArgAt<object>(1)));
                        return Task.FromResult(cursor);
                    });
                return await work(runner);
            });
        return (new Neo4jEntityRepository(txRunner, NullLogger<Neo4jEntityRepository>.Instance), calls);
    }

    private static Entity MakeEntity(string id = "e-1", double? lat = null, double? lon = null) => new()
    {
        EntityId = id,
        Name = "TestEntity",
        Type = "LOCATION",
        Confidence = 0.9,
        CreatedAtUtc = DateTimeOffset.UtcNow,
        Latitude = lat,
        Longitude = lon
    };

    // ── UpsertAsync — location persistence ──

    [Fact]
    public async Task UpsertAsync_WithLatLon_SetsLocationPoint()
    {
        var (repo, calls) = CreateEntityUpsertCapture();
        var entity = MakeEntity(lat: 51.5074, lon: -0.1278);

        await repo.UpsertAsync(entity);

        var locationCall = calls.FirstOrDefault(c => c.Cypher.Contains("e.location = point("));
        locationCall.Cypher.Should().NotBeNull();
        locationCall.Cypher.Should().Contain("latitude: $lat");
        locationCall.Cypher.Should().Contain("longitude: $lon");
    }

    [Fact]
    public async Task UpsertAsync_WithLatLon_PassesCorrectCoordinates()
    {
        var (repo, calls) = CreateEntityUpsertCapture();
        var entity = MakeEntity(lat: 48.8566, lon: 2.3522);

        await repo.UpsertAsync(entity);

        var locationCall = calls.First(c => c.Cypher.Contains("e.location = point("));
        var param = locationCall.Parameters!;
        param.GetType().GetProperty("lat")!.GetValue(param).Should().Be(48.8566);
        param.GetType().GetProperty("lon")!.GetValue(param).Should().Be(2.3522);
    }

    [Fact]
    public async Task UpsertAsync_WithoutLatLon_DoesNotSetLocationPoint()
    {
        var (repo, calls) = CreateEntityUpsertCapture();
        var entity = MakeEntity(); // no lat/lon

        await repo.UpsertAsync(entity);

        calls.Should().NotContain(c => c.Cypher.Contains("e.location = point("));
    }

    // ── SearchByLocationAsync ──

    [Fact]
    public async Task SearchByLocationAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateEntityListReadCapture();

        await repo.SearchByLocationAsync(51.5, -0.1, 10.0);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("point.distance(e.location, point({latitude: $lat, longitude: $lon}))");
        calls[0].Cypher.Should().Contain("$radiusMeters");
    }

    [Fact]
    public async Task SearchByLocationAsync_PassesRadiusInMeters()
    {
        var (repo, calls) = CreateEntityListReadCapture();

        await repo.SearchByLocationAsync(51.5, -0.1, radiusKm: 5.0);

        var param = calls[0].Parameters!;
        var radiusMeters = (double)param.GetType().GetProperty("radiusMeters")!.GetValue(param)!;
        radiusMeters.Should().Be(5000.0);
    }

    [Fact]
    public async Task SearchByLocationAsync_PassesLimitParameter()
    {
        var (repo, calls) = CreateEntityListReadCapture();

        await repo.SearchByLocationAsync(0, 0, 1.0, limit: 7);

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("limit")!.GetValue(param).Should().Be(7);
    }

    // ── SearchInBoundingBoxAsync ──

    [Fact]
    public async Task SearchInBoundingBoxAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreateEntityListReadCapture();

        await repo.SearchInBoundingBoxAsync(50.0, -1.0, 52.0, 1.0);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("point.withinBBox(");
        calls[0].Cypher.Should().Contain("$minLon");
        calls[0].Cypher.Should().Contain("$maxLon");
    }

    [Fact]
    public async Task SearchInBoundingBoxAsync_PassesAllBoundsAsParameters()
    {
        var (repo, calls) = CreateEntityListReadCapture();

        await repo.SearchInBoundingBoxAsync(minLat: 50.0, minLon: -1.0, maxLat: 52.0, maxLon: 1.0);

        var param = calls[0].Parameters!;
        param.GetType().GetProperty("minLat")!.GetValue(param).Should().Be(50.0);
        param.GetType().GetProperty("minLon")!.GetValue(param).Should().Be(-1.0);
        param.GetType().GetProperty("maxLat")!.GetValue(param).Should().Be(52.0);
        param.GetType().GetProperty("maxLon")!.GetValue(param).Should().Be(1.0);
    }

    // ── GetPageWithoutEmbeddingAsync ──

    [Fact]
    public async Task GetPageWithoutEmbeddingAsync_SendsCorrectCypher()
    {
        var (repo, calls) = CreatePagedEntityReadCapture();

        await repo.GetPageWithoutEmbeddingAsync(50);

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("e.embedding IS NULL");
        calls[0].Cypher.Should().Contain("LIMIT $limit");
    }

    // ── UpdateEmbeddingAsync ──

    [Fact]
    public async Task UpdateEmbeddingAsync_SendsSetEmbeddingCypher()
    {
        var (repo, calls) = CreateWriteCapture();

        await repo.UpdateEmbeddingAsync("entity-1", new float[] { 0.1f, 0.2f });

        calls.Should().ContainSingle();
        calls[0].Cypher.Should().Contain("SET e.embedding = $embedding");
    }
}
