using System.Security.Cryptography;
using System.Text;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Resolves the physical Neo4j database name for a memory store (the application tier, R1b) and
/// sanitizes application ids into valid Neo4j database names.
/// </summary>
/// <remarks>
/// Neo4j database-naming rules (5.x): 3–63 characters; the first character must be an ASCII letter;
/// remaining characters may be ASCII letters, digits, dots, or dashes; the name is lowercased and
/// must not end with a dot. Underscores are not permitted — hence the default prefix is <c>mem-</c>.
/// </remarks>
internal static class StoreDatabaseNaming
{
    private const int MaxLength = 63;
    private const int MinLength = 3;

    /// <summary>
    /// Resolves the database for the given <paramref name="applicationId"/>:
    /// <list type="bullet">
    /// <item>SharedDatabase, or a null/empty application id ⇒ the default store database
    /// (<see cref="MemoryStoreOptions.DefaultDatabase"/>, or <paramref name="fallbackDatabase"/> when blank).</item>
    /// <item>DatabasePerApplication with a concrete id ⇒ <c>DatabasePrefix + sanitize(applicationId)</c>.</item>
    /// </list>
    /// </summary>
    public static string Resolve(MemoryStoreOptions options, string fallbackDatabase, string? applicationId)
    {
        var defaultDb = string.IsNullOrWhiteSpace(options.DefaultDatabase)
            ? fallbackDatabase
            : options.DefaultDatabase;

        if (options.Strategy != MemoryStorageStrategy.DatabasePerApplication
            || string.IsNullOrWhiteSpace(applicationId))
        {
            return defaultDb;
        }

        var sanitized = Sanitize(applicationId);

        // An id that sanitizes to nothing (all-punctuation, emoji, etc.) must NOT collapse onto the
        // bare prefix — otherwise every such id would map to the same database and silently share a
        // store. Fall back to a deterministic hash of the original so distinct ids stay distinct.
        if (sanitized.Length == 0)
            sanitized = "h" + ShortHash(applicationId, 12);

        return Normalize(options.DatabasePrefix + sanitized, applicationId);
    }

    /// <summary>
    /// Maps an arbitrary string to the Neo4j-legal character set: lowercased, with every character
    /// outside <c>[a-z0-9.-]</c> replaced by a dash, then leading/trailing dots and dashes trimmed.
    /// </summary>
    public static string Sanitize(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value.ToLowerInvariant())
        {
            var ok = (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9') || ch == '.' || ch == '-';
            var mapped = ok ? ch : '-';

            // Collapse consecutive dashes so a run of illegal characters yields a single separator.
            if (mapped == '-' && sb.Length > 0 && sb[^1] == '-')
                continue;

            sb.Append(mapped);
        }
        return sb.ToString().Trim('.', '-');
    }

    // Enforces the structural rules on the composed name. When truncation is required, a short
    // deterministic hash of the original application id is appended so two long ids cannot collide
    // onto the same database (which would silently merge two stores — the very leak R1b prevents).
    private static string Normalize(string name, string applicationId)
    {
        // First character must be an ASCII letter.
        if (name.Length == 0 || name[0] is < 'a' or > 'z')
            name = "m" + name;

        if (name.Length > MaxLength)
        {
            const int hashLen = 8;
            var hash = ShortHash(applicationId, hashLen);
            name = name[..(MaxLength - hashLen - 1)].TrimEnd('.', '-') + "-" + hash;
        }

        name = name.TrimEnd('.');

        if (name.Length < MinLength)
            name = name.PadRight(MinLength, 'x');

        return name;
    }

    private static string ShortHash(string value, int hexChars)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        var sb = new StringBuilder(hexChars);
        for (var i = 0; sb.Length < hexChars && i < bytes.Length; i++)
            sb.Append(bytes[i].ToString("x2"));
        return sb.ToString(0, hexChars);
    }
}
