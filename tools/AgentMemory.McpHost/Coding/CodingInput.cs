using System.Security.Cryptography;
using System.Text.Json;

namespace AgentMemory.McpHost.Coding;

internal static class CodingInput
{
    public static void RequireText(string value, int maximumLength, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > maximumLength)
            throw new ArgumentException($"{name} must contain 1 to {maximumLength} characters.", name);
    }

    public static string SessionId(CodingIdentity identity, string session) =>
        "coding-" + Convert.ToHexStringLower(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(new[] { identity.OwnerId, identity.ProjectId, session })));
}
