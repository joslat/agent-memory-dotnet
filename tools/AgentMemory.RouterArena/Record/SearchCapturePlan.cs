using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Record;

internal sealed record SearchCaptureQuery
{
    public required string Id { get; init; }
    public required int Session { get; init; }
    public required string Question { get; init; }
    public bool Historical { get; init; }
}

internal sealed class SearchCapturePlan
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private sealed record Document
    {
        public required int SchemaVersion { get; init; }
        public required string Role { get; init; }
        public required int WideLimit { get; init; }
        public required IReadOnlyList<SearchCaptureQuery> Cases { get; init; }
    }
    private readonly string _path;
    public string FileSha256 { get; }
    public string Role { get; }
    public int WideLimit { get; }
    public IReadOnlyList<SearchCaptureQuery> Cases { get; }

    private SearchCapturePlan(string path, byte[] bytes, Document document)
    {
        _path = path;
        FileSha256 = Convert.ToHexStringLower(SHA256.HashData(bytes));
        Role = document.Role;
        WideLimit = document.WideLimit;
        Cases = document.Cases;
    }

    public static async Task<SearchCapturePlan> ReadAsync(string path, IReadOnlyList<StoreSessions.Turn> turns, CancellationToken cancellationToken)
    {
        var bytes = await BoundedJson.ReadAsync(path, cancellationToken).ConfigureAwait(false);
        var document = JsonSerializer.Deserialize<Document>(bytes, Json) ?? throw new FormatException("Missing search plan.");
        var sessions = turns.Select(t => t.Session).ToHashSet();
        if (document.SchemaVersion != 1 || document.Role is not ("development" or "synthetic-development") ||
            document.WideLimit is < 1 or > 512 || document.Cases is not { Count: > 0 and <= 128 } ||
            document.Cases.Any(c => c is null || string.IsNullOrWhiteSpace(c.Id) || c.Id.Length > 128 ||
                string.IsNullOrWhiteSpace(c.Question) || c.Question.Length > 8000 || !sessions.Contains(c.Session)) ||
            document.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != document.Cases.Count)
            throw new FormatException("Invalid development search plan or unselected session.");
        return new(path, bytes, document);
    }

    public void VerifyInput()
    {
        if (StoreSessionEvidence.HashFile(_path) != FileSha256)
            throw new InvalidOperationException("Search plan changed after fingerprinting.");
    }
}
