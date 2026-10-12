using System.Text.RegularExpressions;

namespace AgentMemory.McpHost.Coding;

/// <summary>Explicit local coding-service configuration; secrets are environment-variable references only.</summary>
internal sealed record CodingServiceOptions
{
    public required string OwnerId { get; init; }
    public required IReadOnlyDictionary<string, string> Projects { get; init; }
    public string Url { get; init; } = "http://127.0.0.1:5234";
    /// <summary>Opt-in local memory page. Off registers no page routes and rejects every browser Origin.</summary>
    public bool EnableBrowserUi { get; init; }
    public IReadOnlyList<CodingJudgeOptions> Judges { get; init; } = [];

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(OwnerId) || OwnerId.Length > 256 ||
            Projects is not { Count: > 0 and <= 32 } ||
            Projects.Any(p => p.Key.Length > 32 || !Regex.IsMatch(p.Key, "\\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\\z") || string.IsNullOrWhiteSpace(p.Value)))
            throw new ArgumentException("Coding service requires an owner and canonical project IDs with token-variable names.");
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Host != "127.0.0.1" || uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Local coding service URL must be http://127.0.0.1:<port>.");
    }
}

internal sealed record CodingIdentity(string OwnerId, string ProjectId);

internal sealed record CodingJudgeOptions
{
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public required string Model { get; init; }
    public string? KeyVariable { get; init; }
    public double Weight { get; init; } = 1;
}
