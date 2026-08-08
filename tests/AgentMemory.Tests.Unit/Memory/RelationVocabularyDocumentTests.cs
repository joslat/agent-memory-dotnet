using AgentMemory.Core.Memory;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Memory;

/// <summary>
/// The vocabulary is authored as JSON and shipped as an embedded resource.
/// </summary>
/// <remarks>
/// JSON because it is diffable in review, is what the unifier emits, and can carry per-relation source
/// and licence provenance that a C# array cannot express — this artifact ships inside a NuGet package
/// and draws on schema.org and Wikidata. Embedded rather than a file on disk for the same reason: a
/// file dependency is a deployment hazard and adds a startup I/O failure mode to a library.
/// <para>
/// These tests are the gate. A malformed table must fail CI here rather than surface as a
/// <see cref="TypeInitializationException"/> inside a consumer's process on first use.
/// </para>
/// </remarks>
public sealed class RelationVocabularyDocumentTests
{
    [Fact]
    public void TheEmbeddedDocumentLoads() =>
        RelationVocabularyDocument.Load().Canonical.Should().NotBeEmpty();

    [Fact]
    public void EveryRelationDeclaresItsProvenance()
    {
        // Licence provenance is part of the artifact, not a footnote: this ships in a package and
        // draws on third-party sources.
        foreach (var (relation, entry) in RelationVocabularyDocument.Load().Canonical)
        {
            entry.Sources.Should().NotBeEmpty($"'{relation}' must record where it came from");
            entry.Family.Should().BeOneOf("event", "state");
        }
    }

    [Fact]
    public void CanonicalKeysAreInStoredPredicateKeyForm()
    {
        // Resolution that produced keys the graph cannot match would be worthless.
        foreach (var relation in RelationVocabularyDocument.Load().Canonical.Keys)
            relation.Should().Be(MemoryTripleCanonicalizer.Canonical(relation));
    }

    [Fact]
    public void NoSurfaceFormIsClaimedByTwoRelations()
    {
        // Authoring mistakes must fail the build, not be silently dropped at runtime.
        var document = RelationVocabularyDocument.Load();
        var owners = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new List<string>();
        foreach (var (relation, entry) in document.Canonical)
        {
            foreach (var form in entry.SurfaceForms)
            {
                if (owners.TryGetValue(form, out var existing) && existing != relation)
                    conflicts.Add($"'{form}' claimed by both '{existing}' and '{relation}'");
                else
                    owners[form] = relation;
            }
        }

        conflicts.Should().BeEmpty();
    }

    [Fact]
    public void ASurfaceFormNeverCollidesWithADifferentCanonicalKey()
    {
        var document = RelationVocabularyDocument.Load();
        foreach (var (relation, entry) in document.Canonical)
        {
            foreach (var form in entry.SurfaceForms)
            {
                if (document.Canonical.ContainsKey(form))
                    form.Should().Be(relation, $"'{form}' is itself a canonical relation");
            }
        }
    }

    [Fact]
    public void TheDocumentIsTheSourceTheLexiconAndVocabularyBothUse()
    {
        // One relation known to two layers, one definition. This is the invariant whose absence let
        // `assembled` become resolvable at query time while the extractor was never offered it.
        var document = RelationVocabularyDocument.Load();

        MemoryRelationSeedTable.Table.Keys.Should()
            .BeEquivalentTo(document.Canonical.Keys);
    }

    [Fact]
    public void OpposingRelationsAreBothPresent()
    {
        var canonical = RelationVocabularyDocument.Load().Canonical.Keys;

        foreach (var (left, right) in new[]
                 {
                     ("bought", "sold"), ("likes", "dislikes"),
                     ("borrowed", "lent"), ("gave", "received")
                 })
        {
            canonical.Should().Contain(left).And.Contain(right);
        }
    }

    [Fact]
    public void LoadingIsCachedSoTheParseHappensOnce() =>
        RelationVocabularyDocument.Load().Should().BeSameAs(RelationVocabularyDocument.Load());
}
