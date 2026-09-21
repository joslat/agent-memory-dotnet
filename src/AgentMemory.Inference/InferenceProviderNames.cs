namespace AgentMemory.Inference;

/// <summary>
/// The operator-facing token for each provider, in BOTH directions.
/// </summary>
/// <remarks>
/// <b>One table, parsed and printed from the same place.</b> The token an operator types into
/// <c>AI_INFERENCE_PROVIDER</c> and the token that appears in a run identity (<c>model@provider</c>)
/// have to be the same string, and this repository has paid more than once for two things that must
/// agree being written down twice. A provider added to the enum and forgotten here fails loudly at
/// <see cref="ToToken"/> rather than silently stamping a number with the wrong host.
/// </remarks>
public static class InferenceProviderNames
{
    private static readonly (InferenceProvider Provider, string Token)[] Table =
    [
        (InferenceProvider.AzureOpenAI, "azure"),
        (InferenceProvider.Bitdeer, "bitdeer"),
        (InferenceProvider.OpenAI, "openai"),
        (InferenceProvider.Foundry, "foundry"),
        (InferenceProvider.OpenAICompatible, "openai-compatible"),
    ];

    /// <summary>Every accepted token, in the order they are documented.</summary>
    public static IReadOnlyList<string> AllTokens { get; } = [.. Table.Select(e => e.Token)];

    /// <summary>The token for a provider.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The provider has no token — which means it was added to the enum and not to this table.
    /// Throwing beats returning the enum's name, because the enum name is not the documented token
    /// and a run identity carrying it would look right while naming a host nobody can configure.
    /// </exception>
    public static string ToToken(InferenceProvider provider)
    {
        foreach (var entry in Table)
        {
            if (entry.Provider == provider) return entry.Token;
        }

        throw new ArgumentOutOfRangeException(
            nameof(provider), provider, "No operator token is defined for this provider.");
    }

    /// <summary>Parses an operator-supplied token. Case- and whitespace-insensitive.</summary>
    public static bool TryParse(string? token, out InferenceProvider provider)
    {
        var trimmed = token?.Trim();
        if (!string.IsNullOrEmpty(trimmed))
        {
            foreach (var entry in Table)
            {
                if (string.Equals(entry.Token, trimmed, StringComparison.OrdinalIgnoreCase))
                {
                    provider = entry.Provider;
                    return true;
                }
            }
        }

        provider = InferenceProvider.None;
        return false;
    }
}
