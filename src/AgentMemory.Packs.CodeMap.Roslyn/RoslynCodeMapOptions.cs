// SPDX-License-Identifier: Apache-2.0
namespace AgentMemory.Packs.CodeMap.Roslyn;

/// <summary>How <see cref="RoslynCodeMapReader"/> reads a repository. The defaults are the map as it was measured.</summary>
public sealed record RoslynCodeMapOptions
{
    /// <summary>
    /// CMP-2g, off by default until its trial: a test class named after a type (<c>FooTests</c>, <c>FooTest</c>,
    /// <c>FooShould</c>) also tests that type, when exactly one type of that name is declared outside the test's own project,
    /// after the types the test touches. A test that reaches its subject only through another type
    /// (<c>IdentityHostContextTests</c> calls <c>IdentityIngestion</c>, which calls <c>IdentityHostContext</c>) then links to it.
    /// </summary>
    public bool NamedTestLinks { get; init; }
}
