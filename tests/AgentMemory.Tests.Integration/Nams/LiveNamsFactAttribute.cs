namespace AgentMemory.Tests.Integration.Nams;

/// <summary>
/// A <see cref="FactAttribute"/> that skips at discovery time when <see cref="NamsLiveCredentials.IsAvailable"/>
/// is <see langword="false"/>. These tests call the real NAMS SaaS and must never run in CI or on a machine
/// without an isolated dev workspace configured (engineering plan Phase 10: "never run against production
/// customer workspaces").
/// </summary>
public sealed class LiveNamsFactAttribute : FactAttribute
{
    public LiveNamsFactAttribute()
    {
        // Two distinct reasons to skip, reported distinctly. "Credentials missing" and "nobody asked for
        // these to run" are different states, and collapsing them is how the deprovisioned-workspace
        // failure stayed confusing: the credentials WERE configured, which is exactly why the old message
        // could never have been printed.
        if (!NamsLiveCredentials.IsEnabled)
        {
            Skip = "Live NAMS tests are opt-in: set NAMS_LIVE_TESTS=1 to run them. They call the real NAMS "
                + "SaaS, and the dev workspace they targeted was deprovisioned -- so they are off by default "
                + "rather than failing 29 times per run against a service that is gone.";
        }
        else if (!NamsLiveCredentials.IsAvailable)
        {
            Skip = "NAMS_LIVE_TESTS is set but credentials are not configured "
                + "(NAMS_API_KEY / NAMS_DEV_WORKSPACE_ID) -- skipping live NAMS test.";
        }
    }
}
