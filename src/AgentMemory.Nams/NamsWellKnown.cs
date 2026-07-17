namespace AgentMemory.Nams;

/// <summary>
/// Well-known, non-secret values for the public NAMS SaaS at <c>memory.neo4jlabs.com</c>. Only applicable
/// when targeting that public service -- a self-hosted or otherwise-deployed NAMS-compatible endpoint must
/// set <see cref="NamsOptions.Endpoint"/> to its own address instead.
/// </summary>
public static class NamsWellKnown
{
    /// <summary>The public NAMS SaaS REST API base endpoint.</summary>
    public static readonly Uri Endpoint = new("https://memory.neo4jlabs.com/v1");
}
