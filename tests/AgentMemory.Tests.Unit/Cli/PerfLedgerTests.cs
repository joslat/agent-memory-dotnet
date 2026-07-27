using System.Text.Json.Nodes;
using AgentMemory.Cli.Commands;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Cli;

public sealed class PerfLedgerTests
{
    [Fact]
    public async Task Add_DerivesEntryFromSummaryAndAssignsContiguousSequence()
    {
        var root = NewTempDirectory();
        try
        {
            var ledgerPath = WriteLedger(root);
            var original = JsonNode.Parse(await File.ReadAllTextAsync(ledgerPath))!;
            var firstRun = WriteRun(root, "candidate-one", 384);
            var output = new StringWriter();

            var firstExit = await new PerfLedgerCommand(output).ExecuteAsync(
                firstRun, "0", "improvement", ledgerPath);
            var secondRun = WriteRun(root, "candidate-two", 384);
            var secondExit = await new PerfLedgerCommand(output).ExecuteAsync(
                secondRun, "0", "no-effect", ledgerPath);

            firstExit.Should().Be(0);
            secondExit.Should().Be(0);
            var updated = JsonNode.Parse(await File.ReadAllTextAsync(ledgerPath))!.AsObject();
            var entries = updated["entries"]!.AsArray();
            entries.Should().HaveCount(3);
            JsonNode.DeepEquals(entries[0], original["entries"]![0]).Should().BeTrue();

            var first = entries[1]!.AsObject();
            first["seq"]!.GetValue<int>().Should().Be(1);
            first["label"]!.GetValue<string>().Should().Be("candidate-one");
            first["comparedTo"]!.GetValue<int>().Should().Be(0);
            first["verdict"]!.GetValue<string>().Should().Be("improvement");
            first["commit"]!.GetValue<string>().Should().Be("abc123-dirty");
            first["sourceSummarySha256"]!.GetValue<string>().Should().HaveLength(64);
            first["counters"]!["PERF-R-04"]!["neo4j.queries"]!
                .GetValue<long>().Should().Be(9);
            first["fingerprint"]!["embeddingDimensions"]!
                .GetValue<int>().Should().Be(384);
            entries[2]!["seq"]!.GetValue<int>().Should().Be(2);
            output.ToString().Should().Contain("seq 1").And.Contain("seq 2");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Add_RejectsIncomparableFingerprintWithoutChangingLedger()
    {
        var root = NewTempDirectory();
        try
        {
            var ledgerPath = WriteLedger(root);
            var before = await File.ReadAllBytesAsync(ledgerPath);
            var incompatibleRun = WriteRun(root, "wrong-dimensions", 768);

            var act = () => new PerfLedgerCommand(TextWriter.Null).ExecuteAsync(
                incompatibleRun, "0", "improvement", ledgerPath);

            await act.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*embedding dimensions*");
            (await File.ReadAllBytesAsync(ledgerPath)).Should().Equal(before);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Add_RejectsDuplicateSourceWithoutChangingLedger()
    {
        var root = NewTempDirectory();
        try
        {
            var ledgerPath = WriteLedger(root);
            var run = WriteRun(root, "candidate", 384);
            var command = new PerfLedgerCommand(TextWriter.Null);
            (await command.ExecuteAsync(run, "0", "improvement", ledgerPath)).Should().Be(0);
            var beforeDuplicate = await File.ReadAllBytesAsync(ledgerPath);

            var act = () => command.ExecuteAsync(run, "0", "improvement", ledgerPath);

            await act.Should().ThrowAsync<InvalidDataException>()
                .WithMessage("*already exists*");
            (await File.ReadAllBytesAsync(ledgerPath)).Should().Equal(beforeDuplicate);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string NewTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"agentmemory-ledger-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static string WriteLedger(string root)
    {
        var path = Path.Combine(root, "ledger.json");
        File.WriteAllText(
            path,
            """
            {
              "schemaVersion": 1,
              "entries": [
                {
                  "seq": 0,
                  "label": "baseline",
                  "fingerprint": {
                    "profile": "hermetic",
                    "scale": "S",
                    "embeddingDimensions": 384,
                    "embeddingLatencyMs": 0,
                    "modelLatencyMs": 0,
                    "neo4jImage": "neo4j:5.26",
                    "scenarios": [ "PERF-R-04" ]
                  },
                  "counters": {
                    "PERF-R-04": {
                      "neo4j.queries": 9
                    }
                  }
                }
              ]
            }
            """);
        return path;
    }

    private static string WriteRun(string root, string label, int dimensions)
    {
        var run = Path.Combine(root, label);
        Directory.CreateDirectory(run);
        File.WriteAllText(
            Path.Combine(run, "summary.json"),
            $$"""
            {
              "manifest": {
                "runId": "run-{{label}}",
                "label": "{{label}}",
                "startedAtUtc": "2026-07-27T18:00:00Z",
                "profile": "hermetic",
                "scale": "S",
                "scenarios": [ "PERF-R-04" ],
                "environment": {
                  "commit": "abc123-dirty",
                  "embeddingDimensions": {{dimensions}},
                  "embeddingLatencyMs": 0,
                  "modelLatencyMs": 0,
                  "neo4jImage": "neo4j:5.26"
                }
              },
              "qualityGate": {
                "tolerance": 0
              },
              "quality": {
                "recallAtK": 1,
                "mrr": 1,
                "casesWithViolations": 0
              },
              "extractionQuality": {
                "entityPrecision": 1,
                "entityRecall": 1,
                "factPrecision": 1,
                "factRecall": 1,
                "preferencePrecision": 1,
                "preferenceRecall": 1,
                "falsePositiveRate": 0
              },
              "scenarios": [
                {
                  "scenario": "PERF-R-04",
                  "counters": {
                    "neo4j.queries": {
                      "min": 9,
                      "max": 9,
                      "deterministic": true
                    },
                    "items.retrieved": {
                      "min": 43,
                      "max": 43,
                      "deterministic": true
                    }
                  }
                }
              ]
            }
            """);
        return run;
    }
}
