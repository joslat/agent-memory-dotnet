using AgentMemory.Abstractions.Domain;
using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Repositories;
using AgentMemory.Abstractions.Services;
using AgentMemory.Core.Extraction;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AgentMemory.Tests.Unit.Extraction;

public sealed class CorrectedNamePropagationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Only_a_completed_merge_retires_the_source_and_restates_its_facts(bool merged)
    {
        var entities = Substitute.For<IEntityRepository>();
        var facts = Substitute.For<IFactRepository>();
        var embeddings = Substitute.For<IEmbeddingOrchestrator>();
        var clock = Substitute.For<IClock>();
        var ids = Substitute.For<IIdGenerator>();
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var scope = MemoryScope.For("owner", includeShared: false);
        using var cancellation = new CancellationTokenSource();
        var token = cancellation.Token;
        clock.UtcNow.Returns(now);
        ids.GenerateId().Returns("restated");
        var source = new Entity
        {
            EntityId = "old", Name = "Pruya", Type = "PERSON", Confidence = 1,
            CreatedAtUtc = now, OwnerId = "owner",
        };
        var target = source with { EntityId = "new", Name = "Priya" };
        entities.FindLiveByNameAsync("Pruya", null, scope, token).Returns(source);
        entities.FindLiveByNameAsync("Priya", null, scope, token).Returns(target);
        entities.MergeEntitiesAsync("old", "new", scope, token).Returns(merged);
        entities.InvalidateAsync("old", scope, token).Returns(true);
        var replacedName = new Fact
        {
            FactId = "name", Subject = "user", Predicate = "is named", Object = "Pruya",
            CreatedAtUtc = now, Confidence = 1, OwnerId = "owner", InvalidatedAtUtc = now,
        };
        var about = replacedName with
        {
            FactId = "about", Subject = "Pruya", Predicate = "lives in", Object = "Lyon", InvalidatedAtUtc = null,
        };
        facts.GetBySubjectAsync("user", scope, token).Returns(new[] { replacedName });
        facts.GetBySubjectAsync("Pruya", scope, token).Returns(new[] { about });
        facts.GetByObjectAsync("Pruya", scope, token).Returns(Array.Empty<Fact>());
        facts.UpsertAsync(Arg.Any<Fact>(), token).Returns(ci => ci.Arg<Fact>());
        facts.SupersedeAsync("about", "restated", scope, token).Returns(true);
        embeddings.EmbedAsync("Priya", token).Returns(new float[8]);
        embeddings.EmbedFactAsync("Priya", "lives in", "Lyon", token).Returns(new float[8]);
        var options = new ExtractionOptions { RenameOnCorrectedName = true };
        var closings = new ClosingPipeline(facts, Substitute.For<IPreferenceRepository>(),
            Substitute.For<IRelationshipRepository>(), entities, options);
        var sut = new CorrectedNamePropagation(entities, facts, embeddings, clock, ids, options,
            NullLogger.Instance, closings);

        await sut.ApplyAsync(new ExtractionStageResult
        {
            FilteredFacts = [new ExtractedFact { Subject = "user", Predicate = "is named", Object = "Priya" }],
        }, "owner", now, token);

        await entities.Received(1).MergeEntitiesAsync("old", "new", scope, token);
        await entities.Received(merged ? 1 : 0).InvalidateAsync("old", scope, token);
        await entities.Received(merged ? 1 : 0).UpdateEmbeddingAsync("new", Arg.Any<float[]>(), token);
        await facts.Received(merged ? 1 : 0).GetBySubjectAsync("Pruya", scope, token);
        await facts.Received(merged ? 1 : 0).UpsertAsync(Arg.Is<Fact>(f => f.Subject == "Priya" && f.FactId == "restated"), token);
        await facts.Received(merged ? 1 : 0).SupersedeAsync("about", "restated", scope, token);
        if (!merged)
            embeddings.ReceivedCalls().Should().BeEmpty("a declined merge did not clear the target's embedding");
    }
}
