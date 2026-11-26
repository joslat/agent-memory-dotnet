using System.Text.Json;
using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Services;
using AgentMemory.Neo4j.Services;

namespace AgentMemory.Cli.Commands;

/// <summary>
/// <c>owner export|import|erase</c> (root PLAN 40.47, G3): an owner's data as a whole. <c>erase</c> deletes for good and
/// asks for <c>--confirm</c>; <c>export</c> writes the owner's memory to a JSON file; <c>import</c> writes such a file
/// under an owner, with fresh ids. <c>backfill-conversations</c> (RVW-CONVERSATION-OWNER-BACKFILL) gives conversations
/// written before owners were stamped the one owner their memories came from: a dry run unless <c>--apply</c>.
/// </summary>
public sealed class OwnerCommand(IMemoryOwnerDataService owners, TextWriter output, Neo4jConversationOwnerBackfill? backfill = null)
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Unattributed conversations listed by id, at most.</summary>
    public const int ListedConversations = 50;

    public async Task<int> ExecuteAsync(string? action, string? owner, string? file, bool confirm, bool apply = false)
    {
        if (action == "backfill-conversations") return await BackfillAsync(apply).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(owner))
        {
            output.WriteLine("error: owner needs --owner <id>.");
            return 1;
        }
        switch (action)
        {
            case "erase":
                if (!confirm)
                {
                    output.WriteLine($"error: owner erase deletes everything of '{owner}' for good; add --confirm to do it.");
                    return 1;
                }
                var erased = await owners.EraseAsync(owner).ConfigureAwait(false);
                output.WriteLine($"owner erase: {owner}: {erased.Total} nodes deleted ({string.Join(", ", erased.Deleted.Select(d => $"{d.Key} {d.Value}"))})");
                return 0;
            case "export":
                if (file is null) { output.WriteLine("error: owner export needs --file <path>."); return 1; }
                var export = await owners.ExportAsync(owner).ConfigureAwait(false);
                await File.WriteAllTextAsync(file, JsonSerializer.Serialize(export, Json)).ConfigureAwait(false);
                output.WriteLine($"owner export: {owner}: {export.Entities.Count} entities, {export.Facts.Count} facts, {export.Preferences.Count} preferences, {export.Relationships.Count} relationships -> {file}");
                return 0;
            case "import":
                if (file is null) { output.WriteLine("error: owner import needs --file <path>."); return 1; }
                OwnerMemoryExport? read;
                try
                {
                    read = JsonSerializer.Deserialize<OwnerMemoryExport>(await File.ReadAllTextAsync(file).ConfigureAwait(false));
                }
                catch (Exception ex) when (ex is IOException or JsonException)
                {
                    output.WriteLine($"error: owner import: {ex.Message}");
                    return 1;
                }
                if (read is null) { output.WriteLine("error: owner import: the file is empty."); return 1; }
                var imported = await owners.ImportAsync(read, owner).ConfigureAwait(false);
                output.WriteLine($"owner import: {read.OwnerId} -> {owner}: {string.Join(", ", imported.Written.Select(w => $"{w.Key} {w.Value}"))}");
                return 0;
            default:
                output.WriteLine($"error: owner '{action}' is not export, import, erase or backfill-conversations.");
                return 1;
        }
    }

    private async Task<int> BackfillAsync(bool apply)
    {
        if (backfill is null)
        {
            output.WriteLine("error: owner backfill-conversations needs the Neo4j store.");
            return 1;
        }
        var report = await backfill.RunAsync(apply).ConfigureAwait(false);
        var verb = apply ? "stamped with their owner" : "would be stamped with their owner (dry run: add --apply)";
        output.WriteLine($"owner backfill-conversations: {report.Attributed.Count} {verb}; " +
            $"{report.WithoutOwner.Count} with no owned memory and {report.SeveralOwners.Count} with several owners left without one.");
        foreach (var owned in report.Attributed.GroupBy(e => e.Owners[0]).OrderBy(g => g.Key, StringComparer.Ordinal))
            output.WriteLine($"  {owned.Key}: {owned.Count()}");
        foreach (var id in report.WithoutOwner.Take(ListedConversations))
            output.WriteLine($"  no owned memory: {id}");
        if (report.WithoutOwner.Count > ListedConversations)
            output.WriteLine($"  ... and {report.WithoutOwner.Count - ListedConversations} more with no owned memory");
        foreach (var several in report.SeveralOwners.Take(ListedConversations))
            output.WriteLine($"  several owners: {several.ConversationId} ({string.Join(", ", several.Owners)})");
        if (report.SeveralOwners.Count > ListedConversations)
            output.WriteLine($"  ... and {report.SeveralOwners.Count - ListedConversations} more with several owners");
        return 0;
    }
}
