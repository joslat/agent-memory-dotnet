using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentMemory.Validation;

/// <summary>Reads validation packs and checks them before they run.</summary>
public static class ValidationPackReader
{
    /// <summary>The format this reader reads.</summary>
    public const string Format = "agentmemory-pack/1";

    private const string ResourcePrefix = "AgentMemory.Validation.Packs.";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        // Module proposals carry their kind and link targets by name (Assert, Invalidate, Identity, EntityName).
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private static readonly HashSet<string> Precisions = new(StringComparer.OrdinalIgnoreCase) { "day", "month", "year" };
    private static readonly HashSet<string> FactStates = new(StringComparer.Ordinal) { "live", "closed", "absent" };
    private static readonly HashSet<string> EntityStates = new(StringComparer.Ordinal) { "live", "absent", "alias" };
    private static readonly HashSet<string> ClosedAs = new(StringComparer.Ordinal) { "change", "correction" };

    /// <summary>Parses a pack. Throws <see cref="JsonException"/> on JSON that is not a pack.</summary>
    public static ValidationPack Parse(string json) =>
        JsonSerializer.Deserialize<ValidationPack>(json, Json) ?? throw new JsonException("The pack is empty.");

    /// <summary>Reads a pack from a file.</summary>
    public static ValidationPack ReadFile(string path) => Parse(File.ReadAllText(path));

    /// <summary>The core packs, shipped in this assembly, in id order.</summary>
    public static IReadOnlyList<ValidationPack> Core()
    {
        var assembly = typeof(ValidationPackReader).Assembly;
        return [.. assembly.GetManifestResourceNames()
            .Where(n => n.StartsWith(ResourcePrefix, StringComparison.Ordinal) && n.EndsWith(".json", StringComparison.Ordinal))
            .Select(n =>
            {
                using var stream = assembly.GetManifestResourceStream(n)!;
                using var reader = new StreamReader(stream);
                return Parse(reader.ReadToEnd());
            })
            .OrderBy(p => p.Id, StringComparer.Ordinal)];
    }

    /// <summary>What is wrong with a pack's own data, before anything runs; empty when nothing is.</summary>
    public static IReadOnlyList<string> Check(ValidationPack pack)
    {
        var problems = new List<string>();
        void Problem(string text) => problems.Add(text);

        if (pack.Format != Format) Problem($"format is '{pack.Format}', this reader reads '{Format}'");
        if (string.IsNullOrWhiteSpace(pack.Id)) Problem("the pack has no id");
        foreach (var kind in pack.Covers.Where(k => !PackKinds.All.Contains(k))) Problem($"covers names an unknown kind '{kind}'");
        if (pack.Options.Preset is not ("default" or "conversational" or "recommended"))
            Problem($"preset '{pack.Options.Preset}' is not 'default', 'conversational' or 'recommended'");

        var owners = new HashSet<string>(pack.Owners, StringComparer.Ordinal);
        if (owners.Count < 2 || owners.Count != pack.Owners.Count)
            Problem("a pack declares at least two distinct owners: isolation is checked on every question");

        var predicates = new HashSet<string>(pack.Schema.Predicates.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
        var entityTypes = new HashSet<string>(pack.Schema.EntityTypes, StringComparer.OrdinalIgnoreCase);
        var relationshipTypes = new HashSet<string>(pack.Schema.RelationshipTypes, StringComparer.OrdinalIgnoreCase);

        var sessionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var session in pack.Sessions)
        {
            if (!owners.Contains(session.Owner)) Problem($"session '{session.Id}' is owned by undeclared '{session.Owner}'");
            if (!sessionIds.Add(session.Id)) Problem($"session id '{session.Id}' is used twice");
            foreach (var (trace, index) in session.Traces.Select((x, i) => (x, i)))
            {
                if (string.IsNullOrWhiteSpace(trace.Task)) Problem($"session '{session.Id}' trace {index + 1}: no task");
                if (trace.Kind is not ("episode" or "procedure"))
                    Problem($"session '{session.Id}' trace {index + 1}: kind '{trace.Kind}' is not 'episode' or 'procedure'");
                if (session.Shared) Problem($"session '{session.Id}' trace {index + 1}: a shared session carries no traces");
            }
            foreach (var (message, index) in session.Messages.Select((m, i) => (m, i)))
            {
                var where = $"session '{session.Id}' message {index + 1}";
                if (message.Role is not ("user" or "assistant")) Problem($"{where}: role '{message.Role}' is not 'user' or 'assistant'");
                foreach (var fact in message.Facts)
                {
                    if (!predicates.Contains(fact.Predicate)) Problem($"{where}: predicate '{fact.Predicate}' is not in the schema");
                    if (fact.Precision is { } precision && !Precisions.Contains(precision))
                        Problem($"{where}: precision '{precision}' is not day, month or year");
                }
                foreach (var entity in message.Entities.Where(e => !entityTypes.Contains(e.Type)))
                    Problem($"{where}: entity type '{entity.Type}' is not in the schema");
                foreach (var relationship in message.Relationships.Where(r => !relationshipTypes.Contains(r.Type)))
                    Problem($"{where}: relationship type '{relationship.Type}' is not in the schema");
            }
        }

        foreach (var check in pack.Storage)
        {
            var what = check.Fact ?? check.Entity ?? "(nothing)";
            if (!owners.Contains(check.Owner)) Problem($"storage '{what}' names undeclared owner '{check.Owner}'");
            if ((check.Fact is null) == (check.Entity is null)) Problem($"storage '{what}' sets exactly one of fact and entity");
            if (check.Fact is not null)
            {
                if (TryTriple(check.Fact, out _, out var predicate, out _))
                {
                    if (!predicates.Contains(predicate)) Problem($"storage '{what}': predicate '{predicate}' is not in the schema");
                }
                else Problem($"storage '{what}' is not 'subject | predicate | object'");
                if (!FactStates.Contains(check.State)) Problem($"storage '{what}': state '{check.State}' is not live, closed or absent");
                if (check.ClosedAs is { } closedAs && (!ClosedAs.Contains(closedAs) || check.State != "closed"))
                    Problem($"storage '{what}': closedAs '{closedAs}' needs state closed and is change or correction");
            }
            if (check.Entity is not null)
            {
                if (!EntityStates.Contains(check.State)) Problem($"storage '{what}': state '{check.State}' is not live, absent or alias");
                if ((check.State == "alias") != (check.Of is not null)) Problem($"storage '{what}': 'of' goes with state alias, and only with it");
            }
        }

        var questionIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var question in pack.Questions)
        {
            if (!questionIds.Add(question.Id)) Problem($"question id '{question.Id}' is used twice");
            if (!owners.Contains(question.Owner)) Problem($"question '{question.Id}' is asked by undeclared '{question.Owner}'");
            if (question.Expect.Count + question.Exclude.Count == 0) Problem($"question '{question.Id}' expects and excludes nothing");
            if (question.Session is { } asked && !pack.Sessions.Any(x => x.Id == asked && x.Owner == question.Owner))
                Problem($"question '{question.Id}' is asked in '{asked}', which is not a session of '{question.Owner}'");
            foreach (var kind in question.Kinds.Where(k => !PackKinds.All.Contains(k))) Problem($"question '{question.Id}' names an unknown kind '{kind}'");
            foreach (var item in question.Expect.Concat(question.Exclude))
            {
                var set = new[] { item.Fact, item.Entity, item.Relationship, item.Preference, item.Message, item.Working }.Count(v => v is not null);
                if (set != 1) Problem($"question '{question.Id}': an item sets {set} fields, it sets exactly one");
                if (item.Fact is not null && !TryTriple(item.Fact, out _, out _, out _))
                    Problem($"question '{question.Id}': '{item.Fact}' is not 'subject | predicate | object'");
                if (item.Relationship is not null && !TryRelationship(item.Relationship, out _, out _, out _))
                    Problem($"question '{question.Id}': '{item.Relationship}' is not 'Source -[TYPE]-> Target'");
            }
        }
        if (pack.Modules is { } modules) CheckModules(pack, modules, owners, Problem);
        return problems;
    }

    private static readonly HashSet<string> RecordStates = new(StringComparer.Ordinal) { "live", "closed", "absent" };
    private static readonly HashSet<string> WriteStatuses = new(StringComparer.Ordinal) { "Stored", "NoOp", "Invalidated", "Refused" };

    /// <summary>The module part (40.26): known modules, owners and sessions, distinct ids, known states and outcomes.</summary>
    private static void CheckModules(ValidationPack pack, PackModules modules, HashSet<string> owners, Action<string> problem)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in modules.Require)
        {
            if (!required.Add(module.Id)) problem($"module '{module.Id}' is required twice");
            try { _ = AgentMemory.Extensibility.Modules.ModuleVersionRange.Parse(module.Version); }
            catch (Exception exception) when (exception is FormatException or ArgumentException)
            { problem($"module '{module.Id}': '{module.Version}' is not a version range"); }
        }
        if (required.Count == 0) problem("a module part requires at least one module");
        bool Owned(string id) => required.Any(m => AgentMemory.Extensibility.Modules.ModuleIdentity.IsOwnedId(m, id));
        var ids = new HashSet<string>(StringComparer.Ordinal);
        void Unique(string id, string what) { if (!ids.Add(id)) problem($"{what} id '{id}' is used twice"); }
        foreach (var step in modules.Teach)
        {
            Unique(step.Id, "teach");
            var session = pack.Sessions.FirstOrDefault(s => s.Id == step.Session);
            if (session is null) problem($"teach '{step.Id}' names unknown session '{step.Session}'");
            if (!Owned(step.Provider)) problem($"teach '{step.Id}': provider '{step.Provider}' is not owned by a required module");
            if (step.Trigger is not ("EveryTurn" or "OnRequest")) problem($"teach '{step.Id}': trigger '{step.Trigger}' is not EveryTurn or OnRequest");
            if (step.Proposals.Count == 0) problem($"teach '{step.Id}' submits no proposals");
            foreach (var source in step.Proposals.SelectMany(p => p.Source?.MessageIds ?? []))
                if (!source.StartsWith('#') || !int.TryParse(source[1..], out var index) || session is null || index < 0 || index >= session.Messages.Count)
                    problem($"teach '{step.Id}': source '{source}' is not '#n' for a message of session '{step.Session}'");
            foreach (var expect in step.Expect)
            {
                if (expect.Proposal < 0 || expect.Proposal >= step.Proposals.Count) problem($"teach '{step.Id}': expect names proposal {expect.Proposal}");
                if (!WriteStatuses.Contains(expect.Status)) problem($"teach '{step.Id}': status '{expect.Status}' is not Stored, NoOp, Invalidated or Refused");
            }
            foreach (var closing in step.Closings)
                if (closing.Proposal < 0 || closing.Proposal >= step.Proposals.Count || !step.Proposals[closing.Proposal].Replaces.Contains(closing.Key)
                    || !double.IsFinite(closing.Probability) || closing.Probability is < 0 or > 1)
                    problem($"teach '{step.Id}': closing of '{closing.Key}' must answer a replacement proposal {closing.Proposal} names, with a probability in [0, 1]");
        }
        foreach (var record in modules.Records)
        {
            Unique(record.Id, "record");
            if (!owners.Contains(record.Owner)) problem($"record '{record.Id}' names undeclared owner '{record.Owner}'");
            if (!required.Contains(record.Module)) problem($"record '{record.Id}' names module '{record.Module}', which the pack does not require");
            if (!RecordStates.Contains(record.State)) problem($"record '{record.Id}': state '{record.State}' is not live, closed or absent");
            if (record.Properties.Count > 0 && record.State != "live") problem($"record '{record.Id}': properties go with state live");
        }
        foreach (var view in modules.Views)
        {
            Unique(view.Id, "view");
            if (!owners.Contains(view.Owner)) problem($"view '{view.Id}' names undeclared owner '{view.Owner}'");
            if (!required.Contains(view.Module)) problem($"view '{view.Id}' names module '{view.Module}', which the pack does not require");
        }
        if (modules.Records.Count + modules.Views.Count == 0) problem("a module part checks no records and no views");
    }

    /// <summary>Splits <c>"subject | predicate | object"</c>.</summary>
    internal static bool TryTriple(string text, out string subject, out string predicate, out string @object)
    {
        var parts = text.Split('|', 3, StringSplitOptions.TrimEntries);
        (subject, predicate, @object) = parts.Length == 3 ? (parts[0], parts[1], parts[2]) : ("", "", "");
        return parts.Length == 3 && parts.All(p => p.Length > 0);
    }

    /// <summary>Splits <c>"Source -[TYPE]-> Target"</c>.</summary>
    internal static bool TryRelationship(string text, out string source, out string type, out string target)
    {
        source = type = target = "";
        var open = text.IndexOf("-[", StringComparison.Ordinal);
        var close = text.IndexOf("]->", StringComparison.Ordinal);
        if (open <= 0 || close <= open + 2) return false;
        source = text[..open].Trim();
        type = text[(open + 2)..close].Trim();
        target = text[(close + 3)..].Trim();
        return source.Length > 0 && type.Length > 0 && target.Length > 0;
    }
}
