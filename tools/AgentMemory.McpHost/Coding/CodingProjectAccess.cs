using System.Security.Cryptography;
using System.Text;

namespace AgentMemory.McpHost.Coding;

/// <summary>Snapshots project credentials at startup; never retains their plaintext values.</summary>
internal sealed class CodingProjectAccess
{
    private readonly string _owner;
    private readonly Dictionary<string, byte[]> _tokens = new(StringComparer.Ordinal);

    public CodingProjectAccess(CodingServiceOptions options, Func<string, string?> environment)
    {
        options.Validate();
        _owner = options.OwnerId;
        foreach (var (project, variable) in options.Projects)
        {
            var token = environment(variable);
            if (!ValidToken(token)) throw InvalidConfiguration();
            var hash = Hash(token!);
            if (_tokens.Values.Any(existing => CryptographicOperations.FixedTimeEquals(existing, hash)))
                throw InvalidConfiguration();
            _tokens.Add(project, hash);
        }
    }

    public CodingIdentity? Authorize(string project, string authorization)
    {
        const string prefix = "Bearer ";
        if (!_tokens.TryGetValue(project, out var expected) ||
            !authorization.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var token = authorization[prefix.Length..];
        if (!ValidToken(token)) return null;
        return CryptographicOperations.FixedTimeEquals(expected, Hash(token))
            ? new CodingIdentity(_owner, project)
            : null;
    }

    private static bool ValidToken(string? token) =>
        token is { Length: >= 32 and <= 256 } && token.All(c => c is >= '!' and <= '~');

    private static byte[] Hash(string token)
    {
        Span<byte> bytes = stackalloc byte[token.Length];
        Encoding.ASCII.GetBytes(token, bytes);
        try { return SHA256.HashData(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private static ArgumentException InvalidConfiguration() => new(
        "Each coding project requires a distinct bearer token of 32 to 256 printable ASCII characters without spaces.");
}
