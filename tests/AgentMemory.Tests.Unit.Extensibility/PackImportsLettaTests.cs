#pragma warning disable AMEXT001
using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using AgentMemory.Extensibility.Ampack;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>Feature 15, R1: a Letta agent file's memory blocks and files become notes; its secrets are never read.</summary>
public sealed class PackImportsLettaTests
{
    private static readonly PackSigningKey Key = PackAuthoring.CreateKey();
    private const string Secret = "sk-not-a-real-key-0123456789";

    private static string AgentFile() => new JsonObject
    {
        ["agents"] = new JsonArray(new JsonObject
        {
            ["name"] = "helper", ["block_ids"] = new JsonArray("block-0", "block-1", "block-9"),
            ["messages"] = new JsonArray(new JsonObject { ["role"] = "user", ["content"] = "hello" }),
            ["secrets"] = new JsonObject { ["OPENAI_API_KEY"] = Secret },
            ["tool_exec_environment_variables"] = new JsonArray(new JsonObject { ["key"] = "TOKEN", ["value"] = Secret }),
        }),
        ["blocks"] = new JsonArray(
            new JsonObject { ["id"] = "block-0", ["label"] = "persona", ["value"] = "I am a helper." },
            new JsonObject { ["id"] = "block-1", ["label"] = "human", ["value"] = "" },
            new JsonObject { ["id"] = "block-2", ["label"] = "orphan", ["value"] = "No agent holds me." }),
        ["files"] = new JsonArray(new JsonObject { ["file_name"] = "notes.md", ["content"] = "A file's text.", ["file_last_modified_date"] = "2026-01-02T03:04:05Z" }),
    }.ToJsonString();

    private static ImportResult Import(string text) =>
        PackImports.FromLetta(Encoding.UTF8.GetBytes(text), "com.example.letta", "1.0.0", "Example Owner", Key.PrivateKeyPem);

    [Fact]
    public void Blocks_an_agent_holds_and_files_become_notes_and_the_rest_is_reported_by_name()
    {
        var result = Import(AgentFile());
        result.Records.Should().Be(2, "the persona block and the file; the empty block is skipped and the orphan is no agent's");
        result.Skipped.Should().Contain(s => s.Contains("helper/human") && s.Contains("no text")).And.Contain(s => s.Contains("block-9"));
        result.Dropped.Keys.Should().Contain(["agent.messages", "agent.secrets (not read)", "agent.tool_exec_environment_variables (not read)", "blocks no agent holds"]);
    }

    [Fact]
    public void A_secret_never_reaches_the_pack_and_a_file_encoded_twice_reads_alike()
    {
        var once = Import(AgentFile());
        using (var zip = new ZipArchive(new MemoryStream(once.Archive)))
            foreach (var entry in zip.Entries)
            {
                using var reader = new StreamReader(entry.Open());
                reader.ReadToEnd().Should().NotContain(Secret, $"{entry.FullName} holds no secret");
            }
        Import(JsonValue.Create(AgentFile())!.ToJsonString()).Records.Should().Be(once.Records);
    }
}
