namespace AgentMemory.Tests.Integration.Nams;

/// <summary>
/// Reads live NAMS SaaS credentials for the isolated dev/test workspace from the environment. On Windows,
/// falls back to a User-scope read when the process environment block doesn't have the variable --
/// <see cref="Environment.GetEnvironmentVariable(string)"/> only sees what existed when the current process
/// started, so a credential set after the IDE/terminal launched would otherwise require a full restart to
/// become visible here.
/// </summary>
internal static class NamsLiveCredentials
{
    public static string? ApiKey => Read("NAMS_API_KEY");

    public static string? WorkspaceId => Read("NAMS_DEV_WORKSPACE_ID");

    /// <summary>
    /// <see langword="true"/> only when both credentials are present. Tests gate on this via
    /// <see cref="LiveNamsFactAttribute"/> so they skip cleanly instead of failing on machines/CI runs that
    /// (correctly) have no live NAMS access -- these must never run against a shared/production workspace.
    /// </summary>
    public static bool IsAvailable => !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(WorkspaceId);

    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        return OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null;
    }
}
