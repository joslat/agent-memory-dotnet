using AgentMemory.Inference;
using Xunit;

namespace AgentMemory.Tests.Unit.Inference;

/// <summary>
/// Clears EVERY variable the resolver reads, and puts them back afterwards.
/// </summary>
/// <remarks>
/// <para>
/// <b>The whole list, not the ones a given test sets.</b> A developer machine with an ambient
/// <c>OPENAI_API_KEY</c> — extremely normal — would otherwise satisfy the OpenAI provider inside a
/// test that set nothing, and auto-detect would resolve it. The test would pass or fail for a reason
/// that has nothing to do with the code, and only on that machine.
/// </para>
/// <para>
/// It scrubs <see cref="InferenceProviderEnvironment.AllVariables"/> rather than a list of its own,
/// so a variable added to the resolver is scrubbed here the moment it is added. A hand-maintained
/// copy is the thing that silently falls behind.
/// </para>
/// </remarks>
internal sealed class ProviderEnvironmentScope : IDisposable
{
    private readonly Dictionary<string, string?> _saved = [];

    internal ProviderEnvironmentScope()
    {
        foreach (var name in InferenceProviderEnvironment.AllVariables)
        {
            _saved[name] = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    /// <summary>Sets one variable for the duration of the scope.</summary>
    internal ProviderEnvironmentScope Set(string name, string? value)
    {
        Environment.SetEnvironmentVariable(name, value);
        return this;
    }

    public void Dispose()
    {
        foreach (var (name, value) in _saved) Environment.SetEnvironmentVariable(name, value);
    }
}

/// <summary>
/// Serialises the tests that mutate the process environment.
/// </summary>
/// <remarks>
/// Environment variables are process-global. Two of these running in parallel would each scrub the
/// other's setup, and the resulting failure looks like a resolver bug rather than a test-isolation
/// one.
/// </remarks>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class EnvVarTestsCollection
{
    /// <summary>The collection name.</summary>
    public const string Name = "inference-environment";
}
