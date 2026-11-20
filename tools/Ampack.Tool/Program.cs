// SPDX-License-Identifier: Apache-2.0
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace Ampack.Tool;

/// <summary>
/// The command <c>ampack</c>: the Ampack client library with a command line. Exit code 0 means verified (or shown), 1 refused,
/// 2 a usage error or a defect, as the Python and Rust commands of the format answer.
/// </summary>
internal static class Program
{
    internal const string Usage = """
        usage: ampack verify <pack.ampack> <trust.json> [<revocations.json>...]
               ampack inspect <pack.ampack>
               ampack show <pack.ampack> <trust.json> [<revocations.json>...] [--records]
               ampack --version

        verify   says whether your trust policy accepts the pack, and why not when it does not
        inspect  says what the file claims to be; no signature is judged
        show     verifies, then shows the card, the types and the records (--records: one JSON object a line)

        This client verifies and reads memory packs. It does not build, sign or certify them.
        """;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        return Run(args, Console.Out);
    }

    internal static int Run(string[] args, TextWriter output)
    {
        try
        {
            switch (args.FirstOrDefault())
            {
                case "--version":
                    output.WriteLine($"ampack {Version} (.NET client)");
                    return 0;
                case "--help" or "-h":
                    output.WriteLine(Usage);
                    return 0;
                case "verify":
                    return Verify(args[1..], output);
                case "inspect":
                    return Inspect(args[1..], output);
                case "show":
                    return Show(args[1..], output);
                default:
                    output.WriteLine(Usage);
                    return 2;
            }
        }
        catch (PackRefusedException refused)
        {
            output.WriteLine("refused: " + Shown(refused.Message, 4000));
            return 1;
        }
        // Anything else is a defect, never a verdict: an exit code of its own, so a pipeline tells it from a refusal.
        catch (Exception e)
        {
            output.WriteLine($"error: {e.GetType().Name}: {Shown(e.Message, 1000)}");
            return 2;
        }
    }

    private static string Version =>
        (typeof(Program).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0").Split('+')[0];

    // <pack> <trust.json> [<revocations.json>...]: null, with the usage shown, when a path is missing or an option is unknown.
    private static (string Pack, string Trust, PackOptions Options)? PackAndPolicy(string[] args, TextWriter output, params string[] allowed)
    {
        var paths = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
        if (paths.Length < 2 || args.Any(a => a.StartsWith("--", StringComparison.Ordinal) && !allowed.Contains(a)))
        {
            output.WriteLine(Usage);
            return null;
        }
        // A revocation document is read with the same bound the library applies; a file that cannot be read is a refusal.
        var revocations = new List<ReadOnlyMemory<byte>>();
        foreach (var path in paths[2..])
        {
            try { revocations.Add(File.ReadAllBytes(path)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { throw new PackRefusedException([$"The revocation document cannot be read: {e.Message}"]); }
        }
        return (paths[0], paths[1], new PackOptions { Revocations = revocations });
    }

    private static int Verify(string[] args, TextWriter output)
    {
        if (PackAndPolicy(args, output) is not var (path, trust, options)) return 2;
        var verdict = Pack.Verify(path, trust, options);
        if (!verdict.Verified)
        {
            output.WriteLine("refused: " + Shown(string.Join(" ", verdict.Reasons), 4000));
            return 1;
        }
        output.WriteLine($"verified {Shown(verdict.Id!)} {Shown(verdict.Version!)} {verdict.Digest}");
        output.WriteLine($"profiles {Shown(string.Join(", ", verdict.Profiles), 1000)}");
        return 0;
    }

    private static int Inspect(string[] args, TextWriter output)
    {
        if (args.Length != 1 || args[0].StartsWith("--", StringComparison.Ordinal))
        {
            output.WriteLine(Usage);
            return 2;
        }
        var summary = Pack.Inspect(args[0]);
        output.WriteLine($"id        {Shown(summary.Id ?? "(none)")}");
        output.WriteLine($"version   {Shown(summary.Version ?? "(none)")}");
        output.WriteLine($"kind      {Shown(summary.Kind ?? "(none)")}");
        output.WriteLine($"title     {Shown(summary.Title ?? "(none)")}");
        output.WriteLine($"digest    {summary.Digest ?? "(no file list)"}");
        output.WriteLine($"files     {summary.Files} ({summary.Bytes} bytes)");
        output.WriteLine($"envelopes {summary.Envelopes}");
        output.WriteLine("No signature was judged: this is what the file claims, not a verdict. Use 'ampack verify'.");
        return 0;
    }

    private static int Show(string[] args, TextWriter output)
    {
        if (PackAndPolicy(args, output, "--records") is not var (path, trust, options)) return 2;
        var pack = Pack.Load(path, trust, options);
        if (args.Contains("--records"))
        {
            // Records alone, one JSON object a line, for another program: the pack's own, then each member's.
            foreach (var each in (IEnumerable<Pack>)[pack, .. pack.Members])
                foreach (var record in each.Records())
                    output.WriteLine(Line(each.Id, record));
            return 0;
        }
        output.WriteLine($"verified {Shown(pack.Id)} {Shown(pack.Version)} {pack.Digest}");
        Describe(pack, output, "");
        foreach (var member in pack.Members)
        {
            output.WriteLine($"member   {Shown(member.Id)} {Shown(member.Version)} {member.Digest}");
            Describe(member, output, "  ");
        }
        return 0;
    }

    private static void Describe(Pack pack, TextWriter output, string indent)
    {
        output.WriteLine($"{indent}kind     {Shown(pack.Kind)}");
        if (pack.Title is { } title) output.WriteLine($"{indent}title    {Shown(title)}");
        output.WriteLine($"{indent}profiles {Shown(string.Join(", ", pack.Profiles), 1000)}");
        using (var card = pack.Card)
        {
            if (card is not null)
            {
                var root = card.RootElement;
                if (root.TryGetProperty("owner", out var owner) && owner.ValueKind == JsonValueKind.Object && Text(owner, "name") is { } name)
                    output.WriteLine($"{indent}owner    {Shown(name)}");
                if (Text(root, "intendedUse") is { } use) output.WriteLine($"{indent}use      {Shown(use, 600)}");
                if (Text(root, "limitations") is { } limits) output.WriteLine($"{indent}limits   {Shown(limits, 600)}");
            }
        }
        using (var ontology = pack.Ontology)
        {
            if (ontology is not null && ontology.RootElement.TryGetProperty("types", out var types) && types.ValueKind == JsonValueKind.Array)
                output.WriteLine($"{indent}types    {Shown(string.Join(", ", types.EnumerateArray().Select(t => Text(t, "name") ?? "?")), 1000)}");
        }
        var records = pack.Records();
        if (records.Count > 0)
            output.WriteLine($"{indent}records  {records.Count}: "
                + Shown(string.Join(", ", records.GroupBy(r => r.Type, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g => $"{g.Count()} {g.Key}")), 1000));
    }

    private static string Line(string pack, PackRecord record)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("pack", pack);
            json.WriteString("type", record.Type);
            json.WriteString("key", record.Key);
            json.WriteStartObject("properties");
            foreach (var (name, value) in record.Properties)
            {
                json.WritePropertyName(name);
                value.WriteTo(json);
            }
            json.WriteEndObject();
            json.WriteStartArray("links");
            foreach (var link in record.Links)
            {
                json.WriteStartObject();
                json.WriteString("relation", link.Relation);
                json.WriteString("target", link.Target);
                json.WriteEndObject();
            }
            json.WriteEndArray();
            foreach (var (name, value) in record.Extra)
            {
                json.WritePropertyName(name);
                value.WriteTo(json);
            }
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string? Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    // Text from a pack, shown so that a line break, a control or a direction mark in it cannot fake a line of this output.
    internal static string Shown(string text, int max = 200)
    {
        var cut = text.Length > max ? max - (char.IsHighSurrogate(text[max - 1]) ? 1 : 0) : text.Length;
        var shown = new string([.. text.Take(cut).Select(c => char.IsControl(c) || IsBidirectional(c) || c is '\u2028' or '\u2029' ? '?' : c)]);
        return cut < text.Length ? shown + "…" : shown;
    }

    private static bool IsBidirectional(char c) => c is '\u061C' or '\u200E' or '\u200F' or >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069';
}
