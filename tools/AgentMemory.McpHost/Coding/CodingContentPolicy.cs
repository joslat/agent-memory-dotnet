using System.Text.RegularExpressions;

namespace AgentMemory.McpHost.Coding;

/// <summary>Rejects recognizable secrets and bulk input. This is not a guarantee about arbitrary prose.</summary>
internal sealed class CodingContentPolicy
{
    private readonly string[] _knownSecrets;
    private static readonly Regex Secret = new(
        """-----BEGIN (?:[A-Z ]+ )?PRIVATE KEY-----|\bBearer\s+[A-Za-z0-9._~+/-]{16,}={0,2}|\b(?:api[-_ ]?key|access[-_ ]?token|client[-_ ]?secret|password|passwd)\b["']?\s*[:=]\s*["']?[^\s"',;]{8,}|\b(?:gh[pousr]_|github_pat_|sk-(?:proj-|ant-)?)[A-Za-z0-9_-]{16,}""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex TranscriptLine = new(
        "^\\s*\\{\\s*\"(?:role|type)\"\\s*:\\s*\"(?:user|assistant|system)\"",
        RegexOptions.Multiline | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public CodingContentPolicy(IEnumerable<string> knownSecrets)
    {
        _knownSecrets = knownSecrets.Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.Ordinal).ToArray();
        if (_knownSecrets.Length > 64 || _knownSecrets.Any(s => s.Length > 4096))
            throw new ArgumentException("Too many or oversized credential values for the coding input policy.");
    }

    public void Check(CodingRememberRequest request)
    {
        request.Validate();
        foreach (var text in new[] { request.Session, request.TurnId, request.UserText, request.AssistantText, request.Commit, request.Source })
            if (text is not null) CheckText(text);
    }

    public void CheckText(string text)
    {
        if (text.Length > 8000 || text.Count(c => c == '\n') >= 40 ||
            _knownSecrets.Any(secret => text.Contains(secret, StringComparison.Ordinal)) ||
            Secret.IsMatch(text) || TranscriptLine.Matches(text).Count > 1)
            throw new ArgumentException("Content was rejected by the coding input policy.");
    }
}
