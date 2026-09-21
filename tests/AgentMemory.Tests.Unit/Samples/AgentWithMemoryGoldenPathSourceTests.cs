using FluentAssertions;

namespace AgentMemory.Tests.Unit.Samples;

public sealed class AgentWithMemoryGoldenPathSourceTests
{
    [Fact]
    public void GoldenPath_KeepsRealProviderReplacementSeams()
    {
        var source = File.ReadAllText(FindRepoFile("samples/AgentMemory.Sample.AgentWithMemory/Program.cs"));

        // The intent is unchanged and only the helper's name moved: a REAL model, on whichever
        // provider the operator configured, with no mock fallback on either the chat or the
        // embedding side.
        source.Should().Contain("RealModel.TryCreate(",
            "the sample must call a real chat model -- no mock IChatClient fallback");
        source.Should().Contain("AddSingleton<IEmbeddingGenerator<string, Embedding<float>>>",
            "the sample must register a real embedding generator");
        source.Should().NotContain("StubEmbeddingGenerator",
            "the sample must call a real embedding model -- no stub fallback");

        // NEW, AND LOAD-BEARING SINCE THE PROVIDER LAYER. Every sample used to run on Azure's
        // text-embedding-ada-002 at 1536, which is also the Neo4j default, so no sample had to say
        // it. On a 1024-wide model -- Bitdeer's default is one -- leaving it unset builds a vector
        // index that does not match what writes into it, and nothing fails at startup to say so.
        source.Should().Contain("options.EmbeddingDimensions = modelSettings.EmbeddingDimensions",
            "the store's vector width must come from the resolved model, not from a default that "
            + "happened to match the only provider this repository used to support");
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
