using AgentMemory.Extensibility.Ampack;

namespace Ampack;

/// <summary>
/// Imports (feature 15): <c>ampack import json &lt;file&gt; --mapping &lt;m.json&gt;</c> and <c>ampack import text &lt;file&gt; --source &lt;name&gt;</c>
/// write a signed knowledge pack of notes from another tool's memory export, with a derivation naming the source by its
/// digest and a card saying where it came from, and report every field and entry they left out.
/// </summary>
internal static partial class Program
{
    // ampack import json|text <file> --id <id> --version <v> --owner <name> --key <private.pem> --out <pack.ampack> [--mapping <m.json> | --source <name>]
    private static int Import(Options o)
    {
        var kind = o.Positional(0, "import kind (json, text)");
        var file = o.Positional(1, "the export file");
        var (id, version, owner) = (o.Required("id"), o.Value("version") ?? "1.0.0", o.Required("owner"));
        var key = Text(o.Required("key"), "The signing key");
        var output = o.Required("out");
        var source = AgentMemory.Packs.Format.PackReader.ReadFile(file, 64L << 20, "The export");
        ImportResult result;
        try
        {
            result = kind switch
            {
                "json" => PackImports.FromJson(source, ImportMapping.Read(AgentMemory.Packs.Format.PackReader.ReadFile(o.Required("mapping"), 1 << 20, "The mapping")),
                    id, version, owner, key),
                "text" => PackImports.FromText(source, o.Value("source") ?? "an assistant", id, version, owner, key),
                _ => throw new UsageException($"Unknown import kind '{kind}': json, text."),
            };
        }
        catch (InvalidDataException e) { return Report([e.Message], ""); }
        File.WriteAllBytes(output, result.Archive);
        Console.WriteLine($"Imported {result.Records} note(s) into {id} {version}, signed; it is off on a host until its trial passes.\n  wrote   {output}");
        foreach (var (field, count) in result.Dropped) Console.WriteLine($"  dropped field '{Shown(field, 80)}' ({count} entr{(count == 1 ? "y" : "ies")})");
        foreach (var skip in result.Skipped) Console.WriteLine($"  skipped {Shown(skip, 200)}");
        foreach (var finding in result.Findings) Console.WriteLine($"  safety  {Shown(finding, 200)}");
        if (result.Findings.Count != 0) Console.WriteLine("  the safety scan found text to review before this pack is used (ampack scan shows it in full).");
        return 0;
    }
}
