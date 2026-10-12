using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Neo4j.Infrastructure;
using AgentMemory.RouterArena.Record;
using AgentMemory.Tests.Unit.TestHelpers;
using FluentAssertions;
using Neo4j.Driver;
using NSubstitute;

namespace AgentMemory.Tests.Unit.RouterArena;

public sealed class StoreEvidenceTests
{
    [Fact]
    public void Evidence_binds_inputs_and_actual_persisted_messages_and_detects_changed_inputs()
    {
        var folder = Directory.CreateTempSubdirectory("store-evidence-");
        try
        {
            var pack = Path.Combine(folder.FullName, "pack.json");
            foreach (var file in new[] { pack, Path.Combine(folder.FullName, "w3-stores.json"), Path.Combine(folder.FullName, "w3-none.json") })
                File.WriteAllText(file, "{}");
            var evidence = new StoreSessionEvidence(pack, folder.FullName, "w3", folder.FullName);
            var message = new Message
            {
                MessageId = "persisted-id", SessionId = "persisted-session", ConversationId = "conversation",
                TimestampUtc = DateTimeOffset.UnixEpoch, Role = "user", Content = "Actual source text",
            };
            evidence.AddMessage(message, "turn-input", "owner");
            evidence.AddMessage(message, "turn-input", "owner");
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(evidence.Export("owner", new { form = "writer" }, false)));
            var root = json.RootElement;
            root.GetProperty("complete").GetBoolean().Should().BeFalse();
            root.GetProperty("inputs").GetProperty("pack").GetString().Should().Be(StoreSessionEvidence.HashFile(pack));
            root.GetProperty("events").GetArrayLength().Should().Be(1);
            root.GetProperty("events")[0].GetProperty("id").GetString().Should().Be("persisted-id");
            root.GetProperty("events")[0].GetProperty("session").GetString().Should().Be("persisted-session");
            var conflict = () => evidence.AddMessage(message with { Content = "Rewritten text" }, "turn-input", "owner");
            conflict.Should().Throw<InvalidOperationException>();
            evidence.VerifyInputs();
            File.WriteAllText(pack, "{\"changed\":true}");
            var verify = () => evidence.VerifyInputs();
            verify.Should().Throw<InvalidOperationException>();
        }
        finally
        {
            foreach (var file in folder.EnumerateFiles()) file.Delete();
            folder.Delete();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_retains_source_arrays_only_when_explicitly_requested(bool preserveTypes)
    {
        var props = new Dictionary<string, object>
        {
            ["id"] = "fact-1", ["source_message_ids"] = new List<object> { "message-1", "message-2" },
            ["confidence"] = 0.75d, ["count"] = 2L, ["flag"] = true,
            ["created_at"] = new ZonedDateTime(DateTimeOffset.UnixEpoch), ["embedding"] = new[] { 0.1f },
        };
        var record = Substitute.For<IRecord>();
        record["p"].Returns(props);
        record["kind"].Returns(new List<object> { "Fact" });
        var runner = Substitute.For<IAsyncQueryRunner>();
        runner.RunAsync("query", Arg.Any<IDictionary<string, object>>()).Returns(new FakeResultCursor([record]));
        var tx = Substitute.For<INeo4jTransactionRunner>();
        tx.ReadAsync(Arg.Any<Func<IAsyncQueryRunner, Task<List<Dictionary<string, object?>>>>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Func<IAsyncQueryRunner, Task<List<Dictionary<string, object?>>>>>()(runner));

        var rows = await StoreSessions.QueryAsync(tx, ["query"], [], default, preserveTypes);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(rows));
        var exported = json.RootElement[0].GetProperty("props");
        exported.TryGetProperty("embedding", out _).Should().BeFalse();
        exported.GetProperty("created_at").ValueKind.Should().Be(JsonValueKind.String);
        if (preserveTypes)
        {
            exported.GetProperty("source_message_ids").EnumerateArray().Select(x => x.GetString())
                .Should().Equal("message-1", "message-2");
            exported.GetProperty("confidence").GetDouble().Should().Be(0.75);
            exported.GetProperty("count").GetInt64().Should().Be(2);
            exported.GetProperty("flag").GetBoolean().Should().BeTrue();
        }
        else
        {
            exported.GetProperty("source_message_ids").GetString().Should().Be(props["source_message_ids"].ToString());
            exported.GetProperty("confidence").GetString().Should().Be(props["confidence"].ToString());
        }
    }
}
