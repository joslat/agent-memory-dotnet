using System.Text.Json;

namespace AgentMemory.RouterArena.Data;

internal static class BoundedJson
{
    public static async Task<byte[]> ReadAsync(string path, CancellationToken cancellationToken)
    {
        await using var file = File.OpenRead(path);
        var bytes = new byte[2 * 1024 * 1024 + 1];
        var size = await file.ReadAtLeastAsync(bytes, bytes.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
        if (size == bytes.Length) throw new FormatException("Input exceeds 2 MiB.");
        using var json = JsonDocument.Parse(bytes.AsMemory(0, size));
        CheckDuplicates(json.RootElement);
        return bytes[..size];
    }

    private static void CheckDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new FormatException("Duplicate field.");
                CheckDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var child in element.EnumerateArray()) CheckDuplicates(child);
    }
}
