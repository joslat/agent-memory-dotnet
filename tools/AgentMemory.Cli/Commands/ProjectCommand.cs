#pragma warning disable AMEXT001 // Projections are an Extensibility preview feature.
using System.Text.Json;
using AgentMemory.Extensibility.Packs;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// CMP-7: files as projections of the graph. <c>project --pack decisions|architecture|intent|work --root &lt;repo&gt;</c> reads
/// the pack's records from the repository as an import would and writes them as one Markdown file (under <c>--out</c>,
/// <c>docs/memory</c> by default) and, with <c>--agents &lt;AGENTS.md&gt;</c>, a section in that file. A projection edited by
/// hand is never overwritten unless <c>--force</c>; <c>--check</c> writes nothing and fails when anything is not up to date.
/// Offline: no store is read or written.
/// </summary>
internal sealed class ProjectCommand(TextWriter output)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public int Run(string? pack, string? root, string? folder, string? agents, string? ledger, bool check, bool force)
    {
        if (root is null || pack is null) return Usage("project needs --pack <decisions|architecture|intent|work> and --root <repository>.");
        var repository = Path.GetFullPath(root);
        ModuleSnapshot snapshot;
        switch (pack.ToLowerInvariant())
        {
            case "decisions": snapshot = DecisionsCommand.Read(repository, null, out _); break;
            case "architecture": snapshot = ArchitectureCommand.Read(repository, null, out _, out _); break;
            case "intent": snapshot = IntentCommand.Read(repository, null, out _, out _); break;
            case "work":
                if (ledger is null) return Usage("project --pack work needs --ledger <tasks.json>.");
                snapshot = WorkCommand.Read(ledger, repository, null);
                break;
            default: return Usage($"project knows decisions, architecture, intent and work, not {pack}.");
        }
        var projection = ModuleProjection.Render(snapshot, folder ?? "docs/memory");
        var files = check ? ModuleProjection.Check(repository, [projection]) : ModuleProjection.Write(repository, [projection], force);

        bool? agentsEdited = null;
        var agentsUpToDate = true;
        if (agents is not null)
        {
            var path = Path.Combine(repository, agents);
            var existing = File.Exists(path) ? File.ReadAllText(path) : null;
            var updated = ModuleProjection.AgentsSection(existing, snapshot, projection, force, out var edited);
            agentsEdited = edited;
            agentsUpToDate = existing?.Replace("\r\n", "\n", StringComparison.Ordinal) == updated;
            if (!check && !agentsUpToDate && (!edited || force)) File.WriteAllText(path, updated, new System.Text.UTF8Encoding(false));
        }
        output.WriteLine(JsonSerializer.Serialize(new
        {
            module = snapshot.Module.Id, source = snapshot.Source, records = snapshot.Records.Count, files, agents, agentsEdited, agentsUpToDate,
        }, Json));
        var editedAnywhere = files.Any(f => f.State == ProjectionState.Edited) || agentsEdited == true;
        if (check) return files.All(f => f.State == ProjectionState.UpToDate) && agentsUpToDate ? 0 : 1;
        return editedAnywhere && !force ? 1 : 0;
    }

    private int Usage(string message)
    {
        Console.Error.WriteLine("error: " + message);
        return 2;
    }
}
