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

    /// <summary>The explicit opt-in switch. Live NAMS tests do not run unless this is set truthy.</summary>
    /// <remarks>
    /// <para>
    /// <b>Why credentials alone stopped being a sufficient gate.</b> The dev workspace was deprovisioned
    /// while the credentials stayed in the environment, so <c>IsAvailable</c> went on reporting "yes" and
    /// every live test failed with
    /// <c>503 {"error":"workspace_not_provisioned","status":"deprovisioned"}</c> — 29 red tests in every
    /// integration run, none of them describing a defect in this repository.
    /// </para>
    /// <para>
    /// A gate on "are secrets present" answers a question nobody was asking. What the suite needs to know
    /// is whether someone has <i>deliberately</i> pointed it at a live workspace they expect to be up, and
    /// only a person can answer that. Hence opt-in: absent the switch these skip, and a stale credential in
    /// a shell profile can no longer volunteer the suite into calling a service that is gone.
    /// </para>
    /// </remarks>
    public static bool IsEnabled
    {
        get
        {
            var flag = Read("NAMS_LIVE_TESTS");
            return !string.IsNullOrWhiteSpace(flag)
                && (flag.Equals("1", StringComparison.Ordinal)
                    || flag.Equals("true", StringComparison.OrdinalIgnoreCase)
                    || flag.Equals("yes", StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>
    /// <see langword="true"/> only when the opt-in switch is set <b>and</b> both credentials are present.
    /// Tests gate on this via <see cref="LiveNamsFactAttribute"/> so they skip cleanly instead of failing on
    /// machines/CI runs that (correctly) have no live NAMS access -- these must never run against a
    /// shared/production workspace.
    /// </summary>
    public static bool IsAvailable =>
        IsEnabled && !string.IsNullOrWhiteSpace(ApiKey) && !string.IsNullOrWhiteSpace(WorkspaceId);

    private static string? Read(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (!string.IsNullOrWhiteSpace(value)) return value;
        return OperatingSystem.IsWindows() ? Environment.GetEnvironmentVariable(name, EnvironmentVariableTarget.User) : null;
    }
}
