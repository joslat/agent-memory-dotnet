using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleProposalAdmissionTests
{
    private const string Manifest = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.reviews","version":"0.1.0","sdk":"[0.1,0.2)",
         "ontology":{"labelPrefix":"Review","types":[{"name":"ReviewItem","properties":{
           "title":{"type":"string","required":true,"maxLength":8},
           "state":{"type":"string","values":["new","known"]},
           "count":{"type":"int","min":9007199254740993,"max":9223372036854775807},
           "score":{"type":"float","min":0,"max":1},"active":{"type":"bool"},
           "day":{"type":"date"},"at":{"type":"datetime"}}}],
           "relations":[{"name":"REVIEW_ABOUT","from":"ReviewItem","to":"core:Entity","required":true,"max":1}]}}
        """;

    [Theory]
    [InlineData("count", "9007199254740993", true)]
    [InlineData("count", "9007199254740992", false)]
    [InlineData("count", "9223372036854775807", true)]
    [InlineData("count", "9223372036854775808", false)]
    [InlineData("count", "1.5", false)]
    [InlineData("score", "0", true)]
    [InlineData("score", "1", true)]
    [InlineData("score", "1.0001", false)]
    [InlineData("score", "1e999", false)]
    [InlineData("active", "true", true)]
    [InlineData("active", "\"true\"", false)]
    [InlineData("day", "\"2024-02-29\"", true)]
    [InlineData("day", "\"2025-02-29\"", false)]
    [InlineData("at", "\"2026-10-14T12:00:00Z\"", true)]
    [InlineData("at", "\"2026-10-14T12:00:00\"", false)]
    [InlineData("at", "\"$now\"", false)]
    [InlineData("title", "\"$now\"", true)]
    [InlineData("state", "\"NEW\"", false)]
    [InlineData("state", "\"new\"", true)]
    [InlineData("title", "\"123456789\"", false)]
    [InlineData("title", "null", false)]
    [InlineData("state", "null", false)]
    [InlineData("title", "[]", false)]
    [InlineData("owner_id", "\"other-owner\"", false)]
    [InlineData("TITLE", "\"other\"", false)]
    public void ValidatesLiteralPropertiesWithoutRoundingOrEvaluatingPlaceholders(string name, string json, bool accepted)
    {
        var properties = new Dictionary<string, JsonElement> { ["title"] = Json("lesson") };
        properties[name] = Json(json);
        var result = Admit(Proposal() with { Properties = properties });
        result.Accepted.Should().Be(accepted);
        if (!accepted) result.Errors.Should().Contain(e => e.Contains("properties", StringComparison.Ordinal));
    }

    [Fact]
    public void RejectsMissingRequiredPropertyAndForeignType()
    {
        Admit(Proposal() with { Properties = new Dictionary<string, JsonElement>() }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Type = "Entity" }).Accepted.Should().BeFalse();
        Admit(Proposal(), moduleId: "com.other.reviews").Accepted.Should().BeFalse();
    }

    [Fact]
    public void RequiresProvenanceFromRequestButDoesNotPretendItProvesPersistence()
    {
        Admit(Proposal() with { Source = new() { MessageIds = ["foreign"], Excerpt = "lesson" } }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Source = new() { MessageIds = [], Excerpt = "lesson" } }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Source = new() { MessageIds = ["m1", "m1"], Excerpt = "lesson" } }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Source = new() { MessageIds = ["m1"], Excerpt = new string('x', 501) } }).Accepted.Should().BeFalse();
        // An actual store must still check owner/session membership; this is pure shape admission.
        Admit(Proposal()).Accepted.Should().BeTrue();
    }

    [Fact]
    public void SharedWritesNeedExplicitTriggerAndSelectedOwningProvider()
    {
        var shared = Resolve(Manifest.Replace("\"name\":\"ReviewItem\",", "\"name\":\"ReviewItem\",\"scope\":\"shared\","));
        ModuleProposalAdmission.Admit(shared, new() { Id = "com.example.reviews:writer", Trigger = IngestionTrigger.OnRequest }, Request(), Proposal()).Accepted.Should().BeFalse();
        var request = Request() with { Trigger = IngestionTrigger.OnRequest };
        ModuleProposalAdmission.Admit(shared, new() { Id = "com.example.reviews:writer", Trigger = IngestionTrigger.OnRequest }, request, Proposal()).Accepted.Should().BeFalse();
        request = request with { ProviderIds = ["com.example.reviews:writer"] };
        ModuleProposalAdmission.Admit(shared, new() { Id = "com.example.reviews:writer", Trigger = IngestionTrigger.OnRequest }, request, Proposal()).Accepted.Should().BeTrue();
    }

    [Fact]
    public void ExplicitRequestCannotPromoteAnEveryTurnProviderIntoASharedWriter()
    {
        var shared = Resolve(Manifest.Replace("\"name\":\"ReviewItem\",", "\"name\":\"ReviewItem\",\"scope\":\"shared\","));
        var request = Request() with { Trigger = IngestionTrigger.OnRequest, ProviderIds = ["com.example.reviews:writer"] };
        ModuleProposalAdmission.Admit(shared, new() { Id = "com.example.reviews:writer" }, request, Proposal()).Accepted.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("key\nother")]
    public void RejectsInvalidKeys(string key) => Admit(Proposal() with { Key = key }).Accepted.Should().BeFalse();

    [Fact]
    public void EnforcesLinkOwnershipCardinalityAndTargetSelector()
    {
        var link = new ProposedLink { Relation = "REVIEW_ABOUT", Target = "entity-1" };
        Admit(Proposal() with { Links = [link] }).Accepted.Should().BeTrue();
        Admit(Proposal() with { Links = [link with { TargetKind = LinkTargetKind.EntityName }] }).Accepted.Should().BeTrue();
        Admit(Proposal() with { Links = [link, link with { Target = "entity-2" }] }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Links = [link, link] }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Links = [link with { Relation = "OTHER_ABOUT" }] }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Links = [link with { Target = "" }] }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Links = [link with { TargetKind = (LinkTargetKind)99 }] }).Accepted.Should().BeFalse();
        // A version update may preserve an existing required anchor: store validates final links.
        Admit(Proposal() with { Links = [] }).Accepted.Should().BeTrue();
    }

    [Fact]
    public void InvalidateNeedsIdentityAndSourceButCannotSilentlyApplyValuesOrLinks()
    {
        var proposal = Proposal() with { Kind = ProposalKind.Invalidate, Properties = new Dictionary<string, JsonElement>() };
        Admit(proposal).Accepted.Should().BeTrue();
        Admit(Proposal() with { Kind = ProposalKind.Invalidate }).Accepted.Should().BeFalse();
        Admit(proposal with { Links = [new() { Relation = "REVIEW_ABOUT", Target = "entity-1" }] }).Accepted.Should().BeFalse();
        Admit(proposal with { Kind = (ProposalKind)99 }).Accepted.Should().BeFalse();
    }

    [Fact]
    public void AdmittedSnapshotSurvivesDocumentDisposalAndCallerMutationAndRoundTrips()
    {
        using var document = JsonDocument.Parse("\"lesson\"");
        var properties = new Dictionary<string, JsonElement> { ["title"] = document.RootElement };
        var ids = new List<string> { "m1" };
        var links = new List<ProposedLink> { new() { Relation = "REVIEW_ABOUT", Target = "entity-1" } };
        var result = Admit(Proposal() with { Properties = properties, Source = new() { MessageIds = ids, Excerpt = "lesson" }, Links = links });
        result.Accepted.Should().BeTrue();
        document.Dispose(); properties.Clear(); ids.Clear(); links.Clear();
        var copy = result.Proposal!;
        copy.Properties["title"].GetString().Should().Be("lesson");
        copy.Source.MessageIds.Should().Equal("m1"); copy.Links.Should().HaveCount(1);
        var replayed = JsonSerializer.Deserialize<ModuleProposal>(JsonSerializer.Serialize(copy))!;
        Admit(replayed).Accepted.Should().BeTrue();
    }

    [Fact]
    public void MalformedDeserializedValuesAreRefusedRatherThanRepaired()
    {
        Admit(Proposal() with { Properties = null! }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Links = null! }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Source = null! }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Links = [null!] }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Source = new() { MessageIds = null!, Excerpt = "lesson" } }).Accepted.Should().BeFalse();
        using var document = JsonDocument.Parse("1"); var value = document.RootElement; document.Dispose();
        Admit(Proposal() with { Properties = new Dictionary<string, JsonElement> { ["title"] = value } }).Accepted.Should().BeFalse();
        Admit(Proposal() with { Properties = new Dictionary<string, JsonElement> { ["title"] = default } }).Accepted.Should().BeFalse();
    }

    [Fact]
    public void RequestAndProviderChecksRefuseAutomaticOnRequestAndMalformedInput()
    {
        var provider = new IngestionProviderDescriptor { Id = "com.example.reviews:writer", Trigger = IngestionTrigger.OnRequest };
        ModuleProposalAdmission.Admit(Resolve(), provider, Request(), Proposal()).Accepted.Should().BeFalse();
        ModuleProposalAdmission.Admit(Resolve(), provider with { Trigger = (IngestionTrigger)99 }, Request(), Proposal()).Accepted.Should().BeFalse();
        provider = provider with { Trigger = IngestionTrigger.EveryTurn };
        foreach (var request in new[] {
            Request() with { SessionId = "" }, Request() with { Trigger = (IngestionTrigger)99 },
            Request() with { Messages = null! }, Request() with { ProviderIds = null! },
            Request() with { Messages = [new() { Id = "m1", Role = "system", Text = "lesson" }] },
            Request() with { Messages = [new() { Id = "m1", Role = "user", Text = "lesson" }, new() { Id = "m1", Role = "assistant", Text = "lesson" }] }
        }) ModuleProposalAdmission.Admit(Resolve(), provider, request, Proposal()).Accepted.Should().BeFalse();
        var assistant = Request() with { Messages = [new() { Id = "m1", Role = "assistant", Text = "lesson" }] };
        ModuleProposalAdmission.Admit(Resolve(), provider, assistant, Proposal()).Accepted.Should().BeTrue();
    }

    [Fact]
    public void InvalidResolutionAndEntityNamesForModuleTargetsAreRefused()
    {
        var provider = new IngestionProviderDescriptor { Id = "com.example.reviews:writer" };
        var bad = ModuleResolver.Resolve([ModuleManifest.Parse(Manifest)], ModuleVersion.Parse("0.1.0"), new HashSet<string>());
        ModuleProposalAdmission.Admit(bad, provider, Request(), Proposal()).Accepted.Should().BeFalse();
        var localTarget = Resolve(Manifest.Replace("core:Entity", "ReviewItem"));
        var proposal = Proposal() with { Links = [new() { Relation = "REVIEW_ABOUT", Target = "lesson-2", TargetKind = LinkTargetKind.EntityName }] };
        ModuleProposalAdmission.Admit(localTarget, provider, Request(), proposal).Accepted.Should().BeFalse();
        ModuleProposalAdmission.Admit(localTarget, provider, Request(), proposal with { Links = [proposal.Links[0] with { TargetKind = LinkTargetKind.Identity }] }).Accepted.Should().BeTrue();
    }

    private static ModuleProposalAdmissionResult Admit(ModuleProposal proposal, string moduleId = "com.example.reviews") =>
        ModuleProposalAdmission.Admit(Resolve(), new() { Id = moduleId + ":writer" }, Request(), proposal);
    private static ModuleResolution Resolve(string manifest = Manifest) =>
        ModuleResolver.Resolve([ModuleManifest.Parse(manifest)], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
    private static IngestionRequest Request() => new() { SessionId = "session", Owner = "owner", Messages = [new() { Id = "m1", Role = "user", Text = "lesson" }] };
    private static ModuleProposal Proposal() => new() { Type = "ReviewItem", Key = "lesson-1", Properties = new Dictionary<string, JsonElement> { ["title"] = Json("lesson") }, Source = new() { MessageIds = ["m1"], Excerpt = "lesson" } };
    private static JsonElement Json(string value) => value == "lesson" ? JsonSerializer.SerializeToElement(value) : JsonDocument.Parse(value).RootElement.Clone();
}
