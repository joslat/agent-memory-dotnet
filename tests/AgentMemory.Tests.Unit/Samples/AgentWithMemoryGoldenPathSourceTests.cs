using FluentAssertions;

namespace AgentMemory.Tests.Unit.Samples;

public sealed class AgentWithMemoryGoldenPathSourceTests
{
    [Fact]
    public void GoldenPath_KeepsRealProviderReplacementSeams()
    {
        var source = File.ReadAllText(FindRepoFile("samples/AgentMemory.Sample.AgentWithMemory/Program.cs"));

        source.Should().Contain("RealAzureOpenAI.TryCreate(",
            "the sample must call a real Azure OpenAI chat model -- no mock IChatClient fallback");
        source.Should().Contain("GetEmbeddingClient(embeddingDeployment).AsIEmbeddingGenerator()",
            "the sample must call a real Azure OpenAI embedding model -- no StubEmbeddingGenerator fallback");
        source.Should().Contain("sp.GetRequiredService<IChatClient>()",
            "the agent must use the DI-provided chat client rather than constructing it inline");
        source.Should().Contain("WithMemoryIdentity(",
            "provider swaps must not bypass application/user/session/conversation scoping");
        source.Should().Contain("WithMemoryOwnerScoping(",
            "model-invoked memory tools must inherit trusted host identity for the complete invocation (#90), guaranteed automatically rather than via a manually-wrapped BeginOwnerScope");
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
