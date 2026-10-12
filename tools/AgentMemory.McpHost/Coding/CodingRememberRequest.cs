using System.Security.Cryptography;
using System.Text.Json;

namespace AgentMemory.McpHost.Coding;

/// <summary>A bounded finished turn or deliberate digest; source pointers are caller assertions, never opened.</summary>
internal sealed record CodingRememberRequest
{
    public required string Session { get; init; }
    public required string TurnId { get; init; }
    public required string UserText { get; init; }
    public string? AssistantText { get; init; }
    public required string Commit { get; init; }
    public required string Source { get; init; }

    public void Validate()
    {
        CodingInput.RequireText(Session, 256, nameof(Session));
        CodingInput.RequireText(TurnId, 256, nameof(TurnId));
        CodingInput.RequireText(UserText, 8000, nameof(UserText));
        if (AssistantText is not null) CodingInput.RequireText(AssistantText, 8000, nameof(AssistantText));
        CodingInput.RequireText(Source, 512, nameof(Source));
        if (Source.Any(char.IsControl) || Source.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Source must be a short reference, without a URL or control characters.");
        if (Commit is not { Length: 40 or 64 } || !Commit.All(Uri.IsHexDigit))
            throw new ArgumentException("Commit must be a full hexadecimal Git object ID.");
    }

    public string PayloadHash() => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(this)));
}
