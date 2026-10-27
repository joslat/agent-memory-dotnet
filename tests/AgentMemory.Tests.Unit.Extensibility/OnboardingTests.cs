#pragma warning disable AMEXT001
using System.Text.Json;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Packs;
using AgentMemory.Packs.Intent;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// CMP-9: onboarding an existing project. A model's proposals are kept only when they are the module's own records and every
/// quote is in the lines it cites; they wait in a review set; only what a person accepts joins the deterministic snapshot,
/// and what the project's files say always wins.
/// </summary>
public sealed class OnboardingTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 7, 0, 0, TimeSpan.Zero);

    private static readonly Dictionary<string, string> Files = new(StringComparer.Ordinal)
    {
        ["docs/vision.md"] = "# Vision\n\nThe service shall answer a recall in under 50 ms.\nWhen a pack is switched off, the service shall recall nothing from it.\n\nIdea: a weekly digest of what changed.\n",
    };

    private static string? Read(string path) => Files.GetValueOrDefault(path);

    private static JsonElement J(object value) => JsonSerializer.SerializeToElement(value);

    private static ReviewItem Proposal(string key, string quote = "shall answer a recall in under 50 ms", int line = 3) => new()
    {
        Type = "IntentRequirement", Key = key,
        Properties = new Dictionary<string, JsonElement>
        {
            ["text"] = J("The service shall answer a recall in under 50 ms."), ["pattern"] = J("ubiquitous"), ["reference"] = J("docs/vision.md:3"),
            ["status"] = J("proposed"), ["file"] = J("docs/vision.md"), ["source"] = J($"docs/vision.md:{line}"),
        },
        Evidence = [new ReviewEvidence("docs/vision.md", line, line, quote)], Confidence = 0.8, ProposedBy = "model:test", ProposedAt = Now,
    };

    private static ModuleSnapshot Deterministic(params string[] keys) => new()
    {
        Module = IntentPack.Manifest, Dependencies = [], Source = "git:abc",
        Records = [.. keys.Select(k => Proposal(k)).Select(p => new ModuleProposal { Type = p.Type, Key = p.Key, Properties = p.Properties, Source = new ProposalSource { Excerpt = "file" } })],
    };

    [Fact]
    public void A_review_set_writes_in_a_stable_order_and_reads_back_the_same()
    {
        var set = new ReviewSet { Module = IntentPack.Id, Items = [Proposal("zeta"), Proposal("alpha") with { State = ReviewState.Rejected, ReviewedBy = "jose", ReviewedAt = Now, Note = "not ours" }] };

        var text = ModuleReview.Write(set);

        text.Should().NotContain("\r").And.EndWith("\n");
        text.IndexOf("\"alpha\"", StringComparison.Ordinal).Should().BeLessThan(text.IndexOf("\"zeta\"", StringComparison.Ordinal));
        ModuleReview.Write(ModuleReview.Parse(text)).Should().Be(text);
        ModuleReview.Parse(text).Items[0].Note.Should().Be("not ours");
        FluentActions.Invoking(() => ModuleReview.Parse("""{"format":"other","module":"x","items":[]}""")).Should().Throw<FormatException>();
    }

    [Fact]
    public void A_proposal_already_known_or_decided_is_not_proposed_again()
    {
        var set = new ReviewSet { Module = IntentPack.Id, Items = [Proposal("rejected") with { State = ReviewState.Rejected }] };

        var (updated, added, leftOut) = ModuleReview.Propose(set, [Proposal("rejected"), Proposal("from-files"), Proposal("new")], Deterministic("from-files"));

        (added, leftOut).Should().Be((1, 2));
        updated.Items.Single(i => i.Key == "rejected").State.Should().Be(ReviewState.Rejected, "a rejected key stays rejected");
        updated.Items.Single(i => i.Key == "new").State.Should().Be(ReviewState.Pending);
    }

    [Fact]
    public void A_person_accepts_rejects_or_edits_and_a_typo_decides_nothing()
    {
        var set = new ReviewSet { Module = IntentPack.Id, Items = [Proposal("a"), Proposal("b"), Proposal("c")] };

        set = ModuleReview.Decide(set, ["a"], accept: true, "jose", Now);
        set = ModuleReview.Decide(set, ["IntentRequirement:b"], accept: false, "jose", Now, "a guess");
        set = ModuleReview.Decide(set, ["c"], accept: true, "jose", Now, edits: new Dictionary<string, JsonElement> { ["status"] = J("accepted") });

        set.Items.Select(i => i.State).Should().Equal(ReviewState.Accepted, ReviewState.Rejected, ReviewState.Edited);
        set.Items[2].Properties["status"].GetString().Should().Be("accepted");
        set.Items[1].Note.Should().Be("a guess");
        FluentActions.Invoking(() => ModuleReview.Decide(set, ["no-such-key"], true, "jose", Now)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Only_accepted_proposals_join_the_snapshot_and_the_files_win_a_clash()
    {
        var set = new ReviewSet
        {
            Module = IntentPack.Id,
            Items = [Proposal("accepted") with { State = ReviewState.Accepted, ReviewedBy = "jose", ReviewedAt = Now }, Proposal("pending"),
                Proposal("rejected") with { State = ReviewState.Rejected }, Proposal("from-files") with { State = ReviewState.Accepted, ReviewedBy = "jose", ReviewedAt = Now }],
        };

        var merged = ModuleReview.Merge(Deterministic("from-files"), set, out var conflicts);

        merged.Records.Select(r => r.Key).Should().BeEquivalentTo(["from-files", "accepted"]);
        conflicts.Should().Equal("IntentRequirement:from-files");
        var accepted = merged.Records.Single(r => r.Key == "accepted");
        accepted.Source.Excerpt.Should().Contain("Accepted by jose").And.Contain("model:test").And.Contain("docs/vision.md:3-3");
        accepted.Source.Pack!.File.Should().Be("docs/vision.md");
        merged.Problems().Should().BeEmpty("an accepted proposal is a record the pack takes");
        accepted.Properties["source"].GetString().Should().StartWith("docs/vision.md:3 (accepted by jose on").And.Contain("confidence 0.80",
            "the store keeps a record's file and line as its excerpt, so who accepted it travels in its own source property");
        FluentActions.Invoking(() => ModuleReview.Merge(Deterministic(), set with { Module = "io.example.other" }, out _)).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("shall answer a recall in under 50 ms", 3, null)]
    [InlineData("shall   answer a recall\nin under 50 ms", 3, null)]
    [InlineData("shall answer a recall in under 5 ms", 3, "not in")]
    [InlineData("shall answer a recall in under 50 ms", 4, "not in")]
    [InlineData("50 ms", 3, "too little")]
    [InlineData("shall answer a recall in under 50 ms", 99, "which has")]
    public void Evidence_holds_only_when_the_quote_is_in_the_lines_it_cites(string quote, int line, string? problem)
    {
        var problems = ModuleReview.CheckEvidence(Proposal("x", quote, line), Read);

        if (problem is null) problems.Should().BeEmpty();
        else problems.Should().ContainSingle().Which.Should().Contain(problem);
        ModuleReview.CheckEvidence(Proposal("x") with { Evidence = [new ReviewEvidence("docs/missing.md", 1, 1, "anything at all")] }, Read)
            .Should().ContainSingle().Which.Should().Contain("does not have");
    }

    [Fact]
    public async Task The_proposer_keeps_only_records_the_module_takes_with_evidence_that_holds()
    {
        const string reply = """
            Here you are:
            ```json
            {"records":[
             {"type":"IntentRequirement","key":"recall-under-50-ms","properties":{"text":"The service shall answer a recall in under 50 ms.","pattern":"ubiquitous","reference":"vision","status":"proposed","file":"elsewhere.md","line":99},
              "evidence":[{"file":"docs/vision.md","line":3,"endLine":3,"quote":"The service shall answer a recall in under 50 ms."}],"confidence":0.9},
             {"type":"IntentRequirement","key":"invented","properties":{"text":"x","pattern":"ubiquitous","reference":"r","status":"proposed"},
              "evidence":[{"file":"docs/vision.md","line":3,"endLine":3,"quote":"The service shall never be slow."}],"confidence":0.9},
             {"type":"IntentScenario","key":"wrong-type","properties":{},"evidence":[{"file":"docs/vision.md","line":3,"endLine":3,"quote":"shall answer a recall"}]},
             {"type":"IntentRequirement","key":"extra","properties":{"text":"x","colour":"red"},"evidence":[{"file":"docs/vision.md","line":3,"endLine":3,"quote":"shall answer a recall"}]},
             {"type":"IntentRequirement","key":"bad-status","properties":{"text":"x","pattern":"ubiquitous","reference":"r","status":"maybe"},
              "evidence":[{"file":"docs/vision.md","line":4,"endLine":4,"quote":"the service shall recall nothing"}]}
            ]}
            ```
            """;
        var model = new ScriptedChatClient(reply);
        var options = new ProposerOptions { Types = ["IntentRequirement", "IntentIdea"], ProposedBy = "model:test" };

        var run = await ModuleProposer.ProposeAsync(model, IntentPack.Manifest, [new ProposerDocument("docs/vision.md", Files["docs/vision.md"])],
            Deterministic(), options, Read, Now);

        run.Proposed.Should().ContainSingle();
        var kept = run.Proposed[0];
        kept.Key.Should().Be("recall-under-50-ms");
        kept.Properties["file"].GetString().Should().Be("docs/vision.md", "where a record comes from is the lines it cites");
        kept.Properties["source"].GetString().Should().Be("docs/vision.md:3");
        kept.Properties.Should().NotContainKey("line", "IntentRequirement has no line; the model's is ignored");
        kept.Confidence.Should().Be(0.9);
        run.Dropped.Should().HaveCount(4);
        run.Dropped.Should().Contain(d => d.Contains("invented") && d.Contains("not in"));
        run.Dropped.Should().Contain(d => d.Contains("wrong-type") && d.Contains("not a type"));
        run.Dropped.Should().Contain(d => d.Contains("extra") && d.Contains("colour"));
        run.Dropped.Should().Contain(d => d.Contains("bad-status"));
        model.Options!.Reasoning!.Effort.Should().Be(ReasoningEffort.Low, "a reasoning model must not spend its output thinking");
        model.Options.Temperature.Should().Be(0);
    }

    [Fact]
    public void The_prompt_names_the_types_and_what_is_known_and_never_asks_for_locations()
    {
        var options = new ProposerOptions { Types = ["IntentRequirement"], MaximumChunkCharacters = 120 };

        var prompts = ModuleProposer.Prompts(IntentPack.Manifest, [new ProposerDocument("docs/vision.md", Files["docs/vision.md"])], Deterministic("known-one"), options);

        prompts.Should().HaveCountGreaterThan(1, "the document is cut into chunks at line boundaries");
        prompts[0].User.Should().StartWith("docs/vision.md:1| # Vision");
        var system = prompts[0].System;
        system.Should().Contain("IntentRequirement").And.Contain("known-one").And.NotContain("IntentIdea");
        system.Should().NotContain("    file (").And.NotContain("    line (").And.NotContain("    source (");
    }

    [Fact]
    public async Task A_run_stops_at_its_call_budget_and_says_how_much_it_left()
    {
        var model = new ScriptedChatClient("""{"records":[]}""");
        var options = new ProposerOptions { MaximumChunkCharacters = 60, MaximumCalls = 2 };

        var run = await ModuleProposer.ProposeAsync(model, IntentPack.Manifest, [new ProposerDocument("docs/vision.md", Files["docs/vision.md"])], null, options, Read, Now);

        run.Calls.Should().Be(2);
        run.ChunksSkipped.Should().BeGreaterThan(0);
        model.Calls.Should().Be(2);
    }

    private sealed class ScriptedChatClient(string reply) : IChatClient
    {
        public ChatOptions? Options { get; private set; }

        public int Calls { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options = options;
            Calls++;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, reply)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }
}
