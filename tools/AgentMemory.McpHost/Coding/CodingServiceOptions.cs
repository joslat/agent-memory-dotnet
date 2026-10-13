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
    /// <summary>Allow extractors after a failed writer. Off makes no fallback calls; job receipts report the actual path.</summary>
    public bool AllowExtractorFallback { get; init; }
    /// <summary>Judge retrieval when configured endpoints are available. False uses the similarity floor without retrieval judge calls.</summary>
    public bool UseRetrievalGate { get; init; } = true;
    /// <summary>Explicit override for automatic query splitting. Off performs no automatic subquery derivation or searches.</summary>
    public bool AutomaticQuerySplitting { get; init; }
    /// <summary>Exact configured judge name permitted to approve storage closings. Null uses only the host-model closing fallback.</summary>
    public string? ClosingJudgeName { get; init; }
    /// <summary>Explicit HTTPS/OAuth mode. Null adds no OAuth discovery, validation or rate-limit services.</summary>
    public CodingRemoteOptions? Remote { get; init; }
    /// <summary>Opt-in private write receipts. Null creates no journal files/services and preserves baseline receipt/tool bytes.</summary>
    public CodingJournalOptions? WriteJournal { get; init; }
    /// <summary>Opt-in recall receipts. Null adds no receipt services/tools, feedback or additional retrieval diagnostics.</summary>
    public CodingRecallDiagnosticsOptions? RecallDiagnostics { get; init; }
    /// <summary>Opt-in preview/expansion. Null adds no evidence services, tools, cache or additional reads.</summary>
    public CodingEvidenceOptions? Evidence { get; init; }
    public IReadOnlyList<CodingJudgeOptions> Judges { get; init; } = [];

    public void Validate()
    {
        WriteJournal?.Validate();
        RecallDiagnostics?.Validate();
        Evidence?.Validate();
        if (string.IsNullOrWhiteSpace(OwnerId) || OwnerId.Length > 256 ||
            Projects is not { Count: > 0 and <= 32 } ||
            Projects.Any(p => p.Key.Length > 32 || !Regex.IsMatch(p.Key, "\\A[a-z][a-z0-9]*(?:-[a-z0-9]+)*\\z") ||
                (Remote is null ? string.IsNullOrWhiteSpace(p.Value) : p.Value != "")))
            throw new ArgumentException("Coding service requires an owner and canonical project IDs with token-variable names.");
        if (Remote is not null)
        {
            Remote.Validate(Url, EnableBrowserUi);
            return;
        }
        if (!Uri.TryCreate(Url, UriKind.Absolute, out var uri) || uri.Scheme != "http" ||
            uri.Host != "127.0.0.1" || uri.Port is < 1 or > 65535 || uri.AbsolutePath != "/" ||
            uri.Query.Length != 0 || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0)
            throw new ArgumentException("Local coding service URL must be http://127.0.0.1:<port>.");
    }
}

internal sealed record CodingIdentity(string OwnerId, string ProjectId)
{
    public bool CanWrite { get; init; } = true;
}

internal sealed record CodingJudgeOptions
{
    public required string Name { get; init; }
    public required string Endpoint { get; init; }
    public required string Model { get; init; }
    public string? KeyVariable { get; init; }
    public double Weight { get; init; } = 1;
}
