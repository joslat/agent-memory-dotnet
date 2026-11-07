using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using System.Reflection.Emit;
using FluentAssertions;
using NSubstitute;
using Xunit;

namespace AgentMemory.Tests.Unit.Repositories;

/// <summary>
/// #251 for a backend that keeps <see cref="IEntityRepository.FindLiveByNameAsync"/>'s default body: equal matches are
/// ambiguous, but the owner's own entity outranks a shared one and an exact name an alias.
/// </summary>
public sealed class DefaultNameResolutionTests
{
    private static readonly MemoryScope AliceAndShared = MemoryScope.For("alice", includeShared: true);

    private static Entity Person(string name, string? owner, params string[] aliases) => new()
    {
        EntityId = Guid.NewGuid().ToString("N"), Name = name, Type = "PERSON", Confidence = 1,
        CreatedAtUtc = DateTimeOffset.UtcNow, OwnerId = owner, Aliases = aliases,
    };

    private static IEntityRepository Backend(params Entity[] named)
    {
        var repository = Substitute.For<IEntityRepository>();
        repository.GetByNameAsync(Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<MemoryScope?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<Entity>>(named));
        return repository;
    }

    /// <summary>
    /// The interface's own body of <see cref="IEntityRepository.FindLiveByNameAsync"/>, called non-virtually (as a base
    /// call), so a substitute that only answers <see cref="IEntityRepository.GetByNameAsync"/> stands for a backend that
    /// keeps the default.
    /// </summary>
    private static Task<Entity?> Default(IEntityRepository repository, string name, string? type, MemoryScope scope)
    {
        var method = typeof(IEntityRepository).GetMethod(nameof(IEntityRepository.FindLiveByNameAsync))!;
        var body = new DynamicMethod("FindLiveByNameDefault", typeof(Task<Entity?>),
            [typeof(IEntityRepository), typeof(string), typeof(string), typeof(MemoryScope), typeof(CancellationToken)],
            typeof(DefaultNameResolutionTests).Module, skipVisibility: true);
        var il = body.GetILGenerator();
        for (short i = 0; i < 5; i++) il.Emit(OpCodes.Ldarg, i);
        il.Emit(OpCodes.Call, method);
        il.Emit(OpCodes.Ret);
        var call = body.CreateDelegate<Func<IEntityRepository, string, string?, MemoryScope, CancellationToken, Task<Entity?>>>();
        return call(repository, name, type, scope, CancellationToken.None);
    }

    [Fact]
    public async Task TheOwnersEntityOutranksASharedOneWithTheSameName()
    {
        var mine = Person("Ana", "alice");

        var found = await Default(Backend(Person("Ana", null), mine), "Ana", "PERSON", AliceAndShared);

        found!.EntityId.Should().Be(mine.EntityId);
    }

    [Fact]
    public async Task TwoOfTheOwnersEntitiesSharingAnAliasAreAmbiguous() =>
        (await Default(Backend(Person("Dr. Imre Novak", "alice", "the doctor"), Person("Dr. Sofia Bauer", "alice", "the doctor")),
            "the doctor", "PERSON", AliceAndShared)).Should().BeNull();
}
