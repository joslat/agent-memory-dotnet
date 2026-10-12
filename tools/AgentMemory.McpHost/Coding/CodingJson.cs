using System.Text;
using System.Text.Json;

namespace AgentMemory.McpHost.Coding;

/// <summary>Shared bounded, strict configuration input for the service and native hook client.</summary>
internal static class CodingJson
{
    internal const int MaximumConfigurationBytes = 16 * 1024;

    public static T ParseConfiguration<T>(string json)
    {
        if (Encoding.UTF8.GetByteCount(json) > MaximumConfigurationBytes) throw Invalid();
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
            CheckDuplicates(document.RootElement);
            return document.RootElement.Deserialize<T>(CodingApi.Json) ?? throw Invalid();
        }
        catch (JsonException) { throw Invalid(); }
    }

    public static async Task<string> ReadConfigurationAsync(string path, CancellationToken cancellationToken = default)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        var bytes = new byte[MaximumConfigurationBytes + 1];
        var count = await file.ReadAtLeastAsync(bytes, bytes.Length, false, cancellationToken).ConfigureAwait(false);
        if (count > MaximumConfigurationBytes) throw Invalid();
        try { return new UTF8Encoding(false, true).GetString(bytes, 0, count).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException) { throw Invalid(); }
    }

    private static void CheckDuplicates(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                CheckDuplicates(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) CheckDuplicates(item);
    }

    private static ArgumentException Invalid() => new("Invalid coding configuration; use a strict JSON object of at most 16 KiB.");
}
