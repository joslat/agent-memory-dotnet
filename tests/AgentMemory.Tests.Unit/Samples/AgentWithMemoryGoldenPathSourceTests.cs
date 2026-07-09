using FluentAssertions;

namespace AgentMemory.Tests.Unit.Samples;

public sealed class AgentWithMemoryGoldenPathSourceTests
{
    [Fact]
    public void GoldenPath_KeepsRealProviderReplacementSeams()
    {
        var source = File.ReadAllText(FindRepoFile("samples/AgentMemory.Sample.AgentWithMemory/Program.cs"));

        source.Should().Contain("TryAddSingleton<IChatClient, EchoChatClient>()",
            "the offline chat provider must be replaceable by host DI before the sample resolves IChatClient");
        source.Should().Contain("TryAddSingleton<IEmbeddingGenerator<string, Embedding<float>>, StubEmbeddingGenerator>()",
            "the offline embedding provider must be replaceable by a real MEAI embedding generator");
        source.Should().Contain("sp.GetRequiredService<IChatClient>()",
            "the agent must use the DI-provided chat client rather than constructing the mock inline");
        source.Should().Contain("WithMemoryIdentity(",
            "provider swaps must not bypass application/user/session/conversation scoping");
        source.Should().Contain("ownerContext.BeginOwnerScope(userId)",
            "model-invoked memory tools must inherit trusted host identity with real providers too");
    }

    private static string FindRepoFile(string relativePath)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }

        throw new FileNotFoundException($"Could not locate repository file '{relativePath}'.", relativePath);
    }
}
