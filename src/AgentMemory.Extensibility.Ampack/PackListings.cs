// SPDX-License-Identifier: Apache-2.0
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace AgentMemory.Extensibility.Ampack;

/// <summary>A listing document and what the writer had to leave out or shorten to fit the target's schema.</summary>
[Experimental("AMEXT001")]
public sealed record PackListing(string Json, IReadOnlyList<string> Notes);

/// <summary>
/// Listings (feature 17, R3): a verified pack described for a directory, from the pack's own card, views and certificates and
/// nothing else. What a directory needs that a pack cannot know (where it is served, the name it is listed under) is given by
/// the operator. A listing cites only trusted certificates that passed, with the targets they state; a certificate never
/// carries <c>noHarm</c> or safety (features 04 and 12), so a listing never implies them. Writing a listing publishes nothing.
/// Targets: the A2A Agent Card (protocol 1.0), the MCP registry's <c>server.json</c> (schema 2025-12-11), and an OASF record
/// (schema 1.1.0).
/// </summary>
[Experimental("AMEXT001")]
public static partial class PackListings
{
    /// <summary>The extension URI an A2A card names, and the key under which the other listings carry the pack.</summary>
    public const string Extension = "urn:agentmemory:pack:1";

    /// <summary>The MCP registry schema the server.json follows.</summary>
    public const string McpSchema = "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json";

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>An A2A Agent Card for the agent that serves the pack at <paramref name="url"/> (JSON-RPC).</summary>
    public static PackListing A2a(LoadedPack pack, string url)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var notes = new List<string>();
        var card = new JsonObject
        {
            ["name"] = Title(pack),
            ["description"] = Description(pack),
            ["supportedInterfaces"] = new JsonArray(new JsonObject { ["url"] = url, ["protocolBinding"] = "JSONRPC", ["protocolVersion"] = "1.0" }),
            ["version"] = pack.Version,
            ["capabilities"] = new JsonObject
            {
                ["extensions"] = new JsonArray(new JsonObject
                {
                    ["uri"] = Extension, ["description"] = "A signed memory pack, with its digest and the trial certificates it carries.",
                    ["required"] = false, ["params"] = Evidence(pack),
                }),
            },
            ["defaultInputModes"] = new JsonArray("text/plain"),
            ["defaultOutputModes"] = new JsonArray("application/json"),
            ["skills"] = new JsonArray([.. Views(pack).Select(v => (JsonNode)new JsonObject
            {
                ["id"] = v.Id, ["name"] = v.Id, ["description"] = v.Summary ?? $"Records of type {v.Type}.",
                ["tags"] = new JsonArray(pack.Kind, v.Type),
            })]),
        };
        // A2A's provider needs a URL: named only when the card's contact is one.
        if (pack.Card is { } c && Uri.TryCreate(c.OwnerContact, UriKind.Absolute, out var contact) && contact.Scheme is "https" or "http")
            card["provider"] = new JsonObject { ["organization"] = c.Owner, ["url"] = c.OwnerContact };
        else notes.Add("provider left out: A2A needs the owner's URL, and the card's contact is not one");
        if (pack.Card is null) notes.Add("the pack has no card: the description is its manifest's");
        return new(card.ToJsonString(Indented), notes);
    }

    /// <summary>An MCP registry server.json for the server that serves the pack, listed as <paramref name="name"/>.</summary>
    /// <exception cref="ArgumentException">The name is not the registry's reverse-DNS form.</exception>
    public static PackListing McpRegistry(LoadedPack pack, string name)
    {
        ArgumentNullException.ThrowIfNull(pack);
        if (name is null || name.Length is < 3 or > 200 || !RegistryName().IsMatch(name))
            throw new ArgumentException("The MCP registry name is namespace/name in reverse-DNS form, as io.github.owner/pack (3 to 200 characters).", nameof(name));
        var notes = new List<string>();
        var server = new JsonObject
        {
            ["$schema"] = McpSchema, ["name"] = name,
            ["description"] = Fit(Description(pack), 100, "description", notes),
            ["title"] = Fit(Title(pack), 100, "title", notes),
            ["version"] = pack.Version,
            ["_meta"] = new JsonObject { ["io.modelcontextprotocol.registry/publisher-provided"] = new JsonObject { [Extension] = Evidence(pack) } },
        };
        notes.Add("packages left out: how to install ampack serve is the operator's, not the pack's");
        return new(server.ToJsonString(Indented), notes);
    }

    /// <summary>An OASF record for the pack, created at <paramref name="createdAt"/>, found at <paramref name="url"/> when given.</summary>
    public static PackListing Oasf(LoadedPack pack, DateTimeOffset createdAt, string? url)
    {
        ArgumentNullException.ThrowIfNull(pack);
        var notes = new List<string>();
        var record = new JsonObject
        {
            ["name"] = pack.Id, ["version"] = pack.Version, ["schema_version"] = "1.1.0",
            ["description"] = Description(pack),
            ["authors"] = new JsonArray(pack.Card?.Owner ?? "unnamed"),
            ["created_at"] = createdAt.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            // The one skill a knowledge pack's views give: answering from documents (OASF's taxonomy, by name).
            ["skills"] = new JsonArray(new JsonObject { ["name"] = "ai_ml_engineering/retrieval_augmented_generation/document_or_database_question_answering" }),
            ["annotations"] = new JsonObject
            {
                [Extension + ":id"] = pack.Id, [Extension + ":digest"] = pack.Digest,
                [Extension + ":certificates"] = Evidence(pack)["certificates"]!.ToJsonString(),
            },
        };
        if (url is not null) record["locators"] = new JsonArray(new JsonObject { ["type"] = "url", ["urls"] = new JsonArray(url) });
        if (pack.Card is null) notes.Add("the pack has no card: authors is 'unnamed'");
        notes.Add("skills: OASF's document question answering, by name; its numeric id is not given");
        return new(record.ToJsonString(Indented), notes);
    }

    // The pack's own words for what it is: its card's intended use, else its manifest's description, else its id.
    private static string Description(LoadedPack pack) =>
        pack.Card?.IntendedUse is { Length: > 0 } use ? use : pack.Manifest.Display?.Description is { Length: > 0 } d ? d : pack.Id;

    private static string Title(LoadedPack pack) => pack.Manifest.Display?.Title is { Length: > 0 } t ? t : pack.Id;

    private static IEnumerable<AgentMemory.Extensibility.Modules.ModuleView> Views(LoadedPack pack) => pack.Manifest.Views ?? [];

    // The evidence a listing may cite: the pack's id, version and digest, its limitations, and each trusted certificate that
    // passed, by evaluator, time and the targets it met. Nothing a certificate does not state.
    private static JsonObject Evidence(LoadedPack pack) => new()
    {
        ["id"] = pack.Id, ["version"] = pack.Version, ["digest"] = pack.Digest,
        ["limitations"] = pack.Card?.Limitations,
        ["certificates"] = new JsonArray([.. pack.Certificates.Where(c => c.Trusted && c.Passed).Select(c => (JsonNode)new JsonObject
        {
            ["evaluator"] = c.Evaluator, ["evaluatedAt"] = c.EvaluatedAt?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture),
            ["measure"] = c.Measure, ["baseline"] = c.Baseline, ["confidence"] = c.Confidence,
            ["met"] = new JsonArray([.. c.Checks.Where(k => k.Met).Select(k => (JsonNode)k.Target)]),
        })]),
    };

    // Shortened to the target's limit, and said, never cut silently.
    private static string Fit(string text, int limit, string field, List<string> notes)
    {
        if (new System.Globalization.StringInfo(text).LengthInTextElements <= limit && text.Length <= limit) return text;
        notes.Add($"{field} shortened to the registry's {limit} characters");
        var cut = text[..(limit - 1)];
        if (char.IsHighSurrogate(cut[^1])) cut = cut[..^1];
        return cut + "…";
    }

    [GeneratedRegex(@"^[a-zA-Z0-9.-]+/[a-zA-Z0-9._-]+$")]
    private static partial Regex RegistryName();
}
