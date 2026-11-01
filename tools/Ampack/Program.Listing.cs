using AgentMemory.Extensibility.Ampack;

namespace Ampack;

/// <summary>
/// <c>ampack listing</c> (feature 17, R3): a verified pack described for a directory (A2A, the MCP registry, OASF), from the
/// pack's own card, views and certificates. It writes a file and publishes nothing.
/// </summary>
internal static partial class Program
{
    // ampack listing <pack.ampack> --policy <trust.json> --format a2a|mcp-registry|oasf [--url <where it is served>]
    //   [--name <registry name>] [--at <instant>] [--out <file>]
    private static int Listing(Options o)
    {
        var pack = PackLoader.Load(o.Positional(0, "pack.ampack"), Trust(o));
        var format = o.Required("format");
        PackListing listing;
        try
        {
            listing = format switch
            {
                "a2a" => PackListings.A2a(pack, o.Value("url") ?? throw new UsageException("An A2A card names where the agent is served: give --url.")),
                "mcp-registry" => PackListings.McpRegistry(pack, o.Value("name") ?? throw new UsageException("The MCP registry lists a server by name: give --name, as io.github.owner/pack.")),
                "oasf" => PackListings.Oasf(pack, o.Value("at") is { } at
                    ? DateTimeOffset.TryParseExact(at, "yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal, out var t)
                        ? t : throw new UsageException("--at is an instant in UTC to the second, as 2026-11-01T12:00:00Z.")
                    : DateTimeOffset.UtcNow, o.Value("url")),
                _ => throw new UsageException($"Unknown listing format '{format}': a2a, mcp-registry, oasf."),
            };
        }
        catch (ArgumentException e) { throw new UsageException(e.Message); }
        if (o.Value("out") is { } output) File.WriteAllText(output, listing.Json + "\n");
        else Console.WriteLine(listing.Json);
        foreach (var note in listing.Notes) Console.Error.WriteLine("ampack listing: " + note);
        return 0;
    }
}
