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
        if (!NamsLiveCredentials.IsAvailable)
            Skip = "Live NAMS credentials not configured (NAMS_API_KEY / NAMS_DEV_WORKSPACE_ID) -- skipping live NAMS test.";
    }
}
