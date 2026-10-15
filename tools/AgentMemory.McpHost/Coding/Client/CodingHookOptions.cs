namespace AgentMemory.McpHost.Coding.Client;

internal sealed record CodingHookOptions
{
    public CodingHookHarness Harness { get; init; } = CodingHookHarness.Claude;
    public required string Project { get; init; }
    public required string TokenVariable { get; init; }
    public required string RepositoryRoot { get; init; }
    public required string StateDirectory { get; init; }
    public string ServiceUrl { get; init; } = "http://127.0.0.1:5234";
    public int TimeoutMilliseconds { get; init; } = 5000;
    public IReadOnlyList<string> SecretVariables { get; init; } = [];

    public void Validate()
    {
        new CodingServiceOptions { OwnerId = "client", Projects = new Dictionary<string, string> { [Project] = TokenVariable }, Url = ServiceUrl }.Validate();
        if (!Enum.IsDefined(Harness) || !Path.IsPathFullyQualified(RepositoryRoot) || !Path.IsPathFullyQualified(StateDirectory) ||
            IsWithin(RepositoryRoot, StateDirectory) || TimeoutMilliseconds is < 500 or > 10000 ||
            SecretVariables is not { Count: <= 32 } || SecretVariables.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Hook configuration needs absolute repository/private-state paths, state outside the repository, and a 500..10000 ms timeout.");
    }

    internal static bool IsWithin(string root, string path)
    {
        var relative = Path.GetRelativePath(Path.GetFullPath(root), Path.GetFullPath(path));
        return !Path.IsPathRooted(relative) && relative != ".." && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal);
    }
}

internal enum CodingHookHarness { Claude, VsCodeLocal, CopilotCli, Codex }
