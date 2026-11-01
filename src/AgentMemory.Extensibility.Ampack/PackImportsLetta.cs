using System.Text.Json.Nodes;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>
/// A Letta agent file (<c>.af</c>, Letta's open format) imported (feature 15): each memory block an agent holds becomes a note
/// keyed by the agent's name and the block's label, and each file's text a note keyed by its name. Messages are a conversation,
/// not memory; tools, servers and settings are not records; secrets and tool environment variables are never read. Each is
/// reported by name and count only.
/// </summary>
public static partial class PackImports
{
    /// <summary>Imports a Letta agent file. An agent file has no signature, so its source signature is recorded as absent.</summary>
    /// <exception cref="InvalidDataException">The file is not an agent file, or holds no text to import.</exception>
    public static ImportResult FromLetta(byte[] agentFile, string id, string version, string owner, string signingKeyPem)
    {
        var root = Parse(agentFile, "The agent file");
        // Some published agent files hold their JSON as one JSON string: read once more, and only once.
        if (root is JsonValue encoded && encoded.TryGetValue<string>(out var inner)) root = Parse(System.Text.Encoding.UTF8.GetBytes(inner), "The agent file's inner JSON");
        if (root is not JsonObject file || file["agents"] is not JsonArray agents)
            throw new InvalidDataException("A Letta agent file is an object with its agents.");
        var blocks = (file["blocks"] as JsonArray ?? []).OfType<JsonObject>().Where(b => Str(b, "id") is not null)
            .GroupBy(b => Str(b, "id")!, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var entries = new List<(string Key, string Text, IReadOnlyDictionary<string, string>? Life)>();
        var skipped = new List<string>();
        var dropped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        void Drop(string what, int count) { if (count > 0) dropped[what] = dropped.GetValueOrDefault(what) + count; }
        var held = new HashSet<string>(StringComparer.Ordinal);
        foreach (var agent in agents.OfType<JsonObject>())
        {
            var name = Str(agent, "name") is { Length: > 0 } n ? n : Str(agent, "id") ?? "agent";
            foreach (var blockId in (agent["block_ids"] as JsonArray ?? []).OfType<JsonValue>().Select(v => v.TryGetValue<string>(out var s) ? s : null).OfType<string>())
            {
                if (!blocks.TryGetValue(blockId, out var block)) { skipped.Add($"'{Shown(name)}': block '{Shown(blockId)}' is named but not in the file"); continue; }
                held.Add(blockId);
                var key = $"{name}/{Str(block, "label") ?? blockId}";
                if (Str(block, "value") is not { } text || string.IsNullOrWhiteSpace(text)) { skipped.Add($"'{Shown(key)}': no text"); continue; }
                entries.Add((key, text, null));
            }
            Drop("agent.messages", (agent["messages"] as JsonArray)?.Count ?? 0);
            Drop("agent.tools", (agent["tools"] as JsonArray)?.Count ?? 0);
            // Named and counted, never read: what they hold may be credentials.
            foreach (var secret in new[] { "secrets", "tool_exec_environment_variables" })
                if (agent[secret] is JsonObject or JsonArray) Drop($"agent.{secret} (not read)", 1);
            Drop("agent.system", Str(agent, "system") is { Length: > 0 } ? 1 : 0);
        }
        Drop("blocks no agent holds", blocks.Keys.Count(k => !held.Contains(k)));
        foreach (var f in (file["files"] as JsonArray ?? []).OfType<JsonObject>())
        {
            var name = Str(f, "file_name") ?? Str(f, "original_file_name") ?? Str(f, "id") ?? "file";
            if (Str(f, "content") is not { } text || string.IsNullOrWhiteSpace(text)) { skipped.Add($"'file/{Shown(name)}': no text"); continue; }
            var life = Instant(Str(f, "file_last_modified_date") ?? Str(f, "file_creation_date")) is { } from
                ? new Dictionary<string, string>(StringComparer.Ordinal) { ["validFrom"] = from } : null;
            entries.Add(("file/" + name, text, life));
        }
        foreach (var other in new[] { "groups", "sources", "tools", "mcp_servers", "skills" })
            Drop(other, (file[other] as JsonArray)?.Count ?? 0);
        var duplicate = entries.GroupBy(e => e.Key, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null) throw new InvalidDataException($"Two records would share the key '{Shown(duplicate.Key)}'.");
        return Write(agentFile, entries, skipped, dropped, "letta", "a Letta agent file", id, version, owner, signingKeyPem);
    }
}
