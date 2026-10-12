using AgentMemory.Inference;

namespace AgentMemory.McpHost.Coding;

internal sealed class CodingConfiguration(CodingServiceOptions options, McpHostOptions host,
    CodingProjectAccess access, CodingContentPolicy content, string? certificatePassword = null)
{
    internal const int MaximumBytes = CodingJson.MaximumConfigurationBytes;
    public CodingServiceOptions Options { get; } = options;
    public McpHostOptions Host { get; } = host;
    public CodingProjectAccess Access { get; } = access;
    public CodingContentPolicy Content { get; } = content;
    internal string? CertificatePassword { get; } = certificatePassword;

    public static CodingConfiguration Parse(string json, Func<string, string?> environment)
    {
        var options = CodingJson.ParseConfiguration<CodingServiceOptions>(json);
        options.Validate();
        if (environment(InferenceProviderEnvironment.SelectorVariable)?.Trim().ToLowerInvariant() is not ("bitdeer" or "ollama"))
            throw new ArgumentException("Coding mode requires an explicit Bitdeer or local Ollama provider.");
        foreach (var variable in new[] { "AGENT_MEMORY_MCP_READ_ONLY", "AGENT_MEMORY_MCP_ENABLE_GRAPH_QUERY", "AGENT_MEMORY_MCP_NO_BOOTSTRAP" })
        {
            var value = environment(variable)?.Trim();
            if (!string.IsNullOrEmpty(value) && value != "0" && !value.Equals("false", StringComparison.OrdinalIgnoreCase) &&
                !value.Equals("no", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Coding mode requires incompatible legacy safety/transport flags to be unset or explicitly false.");
        }
        var host = McpHostOptions.Parse([], environment);
        if (host.ReadOnly || host.EnableGraphQuery || !host.Bootstrap || host.Inference.ShowRaw || host.Inference.HasJudgeOverride)
            throw new ArgumentException("Coding mode cannot combine legacy read-only, graph-query, no-bootstrap, raw logging or chat-judge overrides.");
        CheckEndpoint(host.Inference.Provider, host.Inference.Endpoint);
        CheckEndpoint(host.Inference.EmbeddingProvider, host.Inference.EmbeddingEndpoint);
        var access = new CodingProjectAccess(options, environment);
        var secrets = options.Remote is null ? options.Projects.Values.Select(v => environment(v)!).ToList() : new List<string>();
        string? tlsPassword = null;
        if (options.Remote is { } remoteOptions)
        {
            if (environment(remoteOptions.CertificatePasswordVariable) is not { Length: > 0 } password)
                throw new ArgumentException("Remote coding mode requires a certificate password environment variable.");
            secrets.Add(password);
            tlsPassword = password;
        }
        secrets.Add(host.Neo4jPassword);
        if (host.Inference.Provider != InferenceProvider.Ollama || environment("OLLAMA_API_KEY") is not null)
            secrets.Add(host.Inference.ApiKey);
        if (host.Inference.EmbeddingProvider != InferenceProvider.Ollama ||
            environment("AI_EMBEDDING_API_KEY") is not null || environment("OLLAMA_API_KEY") is not null)
            secrets.Add(host.Inference.EmbeddingApiKey!);
        if (options.Judges is not { Count: <= 4 } || options.Judges.Select(j => j?.Name).Distinct(StringComparer.Ordinal).Count() != options.Judges.Count)
            throw Invalid();
        foreach (var judge in options.Judges)
        {
            if (judge is null) throw Invalid();
            CodingInput.RequireText(judge.Name, 32, nameof(judge.Name));
            CodingInput.RequireText(judge.Model, 128, nameof(judge.Model));
            if (!double.IsFinite(judge.Weight) || judge.Weight is <= 0 or > 1000 ||
                !Uri.TryCreate(judge.Endpoint, UriKind.Absolute, out var endpoint) ||
                endpoint.UserInfo.Length != 0 || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 ||
                endpoint.AbsolutePath != "/v1/systemone") throw Invalid();
            var remote = endpoint.Scheme == "https" && endpoint.Host == "api.typesafe.ai" && endpoint.Port == 443;
            if (!remote && !(endpoint.Scheme == "http" && endpoint.Host == "127.0.0.1")) throw Invalid();
            if (remote || judge.KeyVariable is not null)
            {
                if (string.IsNullOrWhiteSpace(judge.KeyVariable) || environment(judge.KeyVariable) is not { Length: > 0 } key)
                    throw new ArgumentException("Each remote coding judge requires a populated key environment variable.");
                secrets.Add(key);
            }
        }
        return new(options, host, access, new(secrets), tlsPassword);
    }

    public static async Task<CodingConfiguration> ReadAsync(string path, Func<string, string?> environment,
        CancellationToken cancellationToken = default)
        => Parse(await CodingJson.ReadConfigurationAsync(path, cancellationToken).ConfigureAwait(false), environment);

    private static void CheckEndpoint(InferenceProvider provider, string? endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.UserInfo.Length != 0 ||
            uri.Query.Length != 0 || uri.Fragment.Length != 0 || uri.AbsolutePath.TrimEnd('/') != "/v1") throw Invalid();
        var allowed = provider switch
        {
            InferenceProvider.Bitdeer => uri.Scheme == "https" && uri.Host == "api-inference.bitdeer.ai" && uri.Port == 443,
            InferenceProvider.Ollama => uri.Scheme == "http" && uri.Host == "127.0.0.1",
            _ => false,
        };
        if (!allowed) throw new ArgumentException("Coding inference endpoints must be Bitdeer HTTPS or IPv4 loopback Ollama /v1 endpoints.");
    }

    private static ArgumentException Invalid() => new("Invalid coding configuration; use a strict JSON object of at most 16 KiB.");
}
