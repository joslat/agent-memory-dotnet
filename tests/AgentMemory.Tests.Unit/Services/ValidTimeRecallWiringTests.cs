using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Services;

/// <summary>
/// <c>RecallOptions.ValidTime</c> must reach the repository, or it is an option that does nothing.
/// </summary>
/// <remarks>
/// The failure mode this guards is the one this track keeps finding: a capability that exists in the
/// options surface, reads correctly in review, and is wired to nothing. The Cypher gate and the enum
/// are both worthless unless the value actually travels the whole chain — assembler → service →
/// repository.
/// </remarks>
public sealed class ValidTimeRecallWiringTests
{
    private readonly IFactRepository _facts = Substitute.For<IFactRepository>();
    private readonly IEntityRepository _entities = Substitute.For<IEntityRepository>();
    private readonly IPreferenceRepository _preferences = Substitute.For<IPreferenceRepository>();
    private readonly IRelationshipRepository _relationships = Substitute.For<IRelationshipRepository>();

    private static readonly IMemoryIsolationPolicy SingleTenant =
        new DefaultMemoryIsolationPolicy(
            Options.Create(new MemoryIsolationOptions()),
            NullLogger<DefaultMemoryIsolationPolicy>.Instance);

    private LongTermMemoryService CreateSut()
    {
        _facts.SearchByVectorAsync(
                Arg.Any<float[]>(), Arg.Any<ValidTimeMode>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Fact, double)>>(Array.Empty<(Fact, double)>()));

        return new LongTermMemoryService(
            _entities, _facts, _preferences, _relationships,
            Substitute.For<IEmbeddingOrchestrator>(),
            Options.Create(new LongTermMemoryOptions()),
            NullLogger<LongTermMemoryService>.Instance,
            SingleTenant);
    }

    [Fact]
    public async Task CurrentReachesTheRepository()
    {
        await CreateSut().SearchFactsAsync(
            new float[] { 1f }, ValidTimeMode.Current, 10, 0.0, null, CancellationToken.None);

        await _facts.Received(1).SearchByVectorAsync(
            Arg.Any<float[]>(), ValidTimeMode.Current, Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task AnUngatedRecallEmitsTheORIGINALRepositoryCall()
    {
        // Byte-identical rather than merely equivalent. An ungated recall must take the overload it
        // always took -- which also means a third-party IFactRepository that never implements the
        // valid-time overload is only ever reached through it when a caller explicitly asked to gate.
        _facts.SearchByVectorAsync(
                Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
                Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<(Fact, double)>>(Array.Empty<(Fact, double)>()));

        await CreateSut().SearchFactsAsync(new float[] { 1f }, 10, 0.0, null, CancellationToken.None);

        await _facts.Received(1).SearchByVectorAsync(
            Arg.Any<float[]>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
        await _facts.DidNotReceive().SearchByVectorAsync(
            Arg.Any<float[]>(), Arg.Any<ValidTimeMode>(), Arg.Any<int>(), Arg.Any<double>(),
            Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void TheOptionDefaultsToIgnore()
    {
        new RecallOptions().ValidTime.Should().Be(ValidTimeMode.Ignore,
            "no existing deployment may silently start recalling less than it did");
        RecallOptions.Default.ValidTime.Should().Be(ValidTimeMode.Ignore);
    }
}
