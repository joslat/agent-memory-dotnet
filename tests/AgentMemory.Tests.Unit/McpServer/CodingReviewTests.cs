#pragma warning disable AMEXT001
using System.Text.Json;
using AgentMemory.Extensibility.Packs;
using AgentMemory.McpHost.Coding;
using AgentMemory.Packs.Intent;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.McpServer;

/// <summary>CMP-9 over MCP: review_list and review_decide read and write one repository's review sets, and only with write scope.</summary>
public sealed class CodingReviewTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("coding-review-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string SetPath => Path.Combine(_root, ModuleReview.DefaultPath(IntentPack.Id));

    private void WriteSet()
    {
        static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);
        var item = new ReviewItem
        {
            Type = "IntentRequirement", Key = "recall-fast",
            Properties = new Dictionary<string, JsonElement>
            {
                ["text"] = J("The service shall answer a recall in under 50 ms."), ["pattern"] = J("ubiquitous"), ["reference"] = J("docs/vision.md:3"),
                ["status"] = J("proposed"), ["file"] = J("docs/vision.md"), ["source"] = J("docs/vision.md:3"),
            },
            Evidence = [new ReviewEvidence("docs/vision.md", 3, 3, "shall answer a recall in under 50 ms")], Confidence = 0.8,
            ProposedBy = "model:test", ProposedAt = DateTimeOffset.UtcNow,
        };
        Directory.CreateDirectory(Path.GetDirectoryName(SetPath)!);
        File.WriteAllText(SetPath, ModuleReview.Write(new ReviewSet { Module = IntentPack.Id, Items = [item, item with { Key = "other" }] }));
    }

    [Fact]
    public void The_review_tools_are_offered_only_when_a_review_folder_is_configured()
    {
        CodingToolDefinitions.For(durable: false).Select(t => t.Name).Should().NotContain("review_list");
        CodingToolDefinitions.For(durable: false, review: true).Select(t => t.Name).Should().Contain(["review_list", "review_decide"]);
    }

    [Fact]
    public void A_decision_is_written_to_the_review_set_with_the_owner_as_reviewer()
    {
        WriteSet();
        var review = new CodingReview(_root);

        JsonSerializer.Serialize(review.List("intent", ReviewState.Pending)).Should().Contain("IntentRequirement:recall-fast").And.Contain("docs/vision.md");
        review.Decide(new CodingIdentity("alex", "p1"), "intent", ["recall-fast"], accept: true,
            new Dictionary<string, JsonElement> { ["status"] = JsonSerializer.SerializeToElement("accepted") }, null).Should().NotBeNull();
        review.Decide(new CodingIdentity("alex", "p1"), "intent", ["other"], accept: false, null, "not a requirement");

        var set = ModuleReview.Parse(File.ReadAllText(SetPath));
        set.Items.Single(i => i.Key == "recall-fast").Should().Match<ReviewItem>(i => i.State == ReviewState.Edited && i.ReviewedBy == "coding:alex");
        set.Items.Single(i => i.Key == "other").Note.Should().Be("not a requirement");
    }

    [Fact]
    public void An_edit_the_pack_would_refuse_is_not_kept()
    {
        WriteSet();
        var before = File.ReadAllText(SetPath);

        FluentActions.Invoking(() => new CodingReview(_root).Decide(new CodingIdentity("alex", "p1"), "intent", ["recall-fast"], accept: true,
                new Dictionary<string, JsonElement> { ["status"] = JsonSerializer.SerializeToElement("maybe") }, null))
            .Should().Throw<ArgumentException>().WithMessage("*refuses*");
        File.ReadAllText(SetPath).Should().Be(before);
        new CodingReview(_root).Decide(new CodingIdentity("alex", "p1"), "decisions", ["x"], true, null, null).Should().BeNull("no review set for that pack");
    }
}
