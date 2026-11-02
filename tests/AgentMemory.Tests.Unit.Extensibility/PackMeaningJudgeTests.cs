#pragma warning disable AMEXT001
using AgentMemory.Extensibility.Neo4j.Storage;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The judge on the host's chat model (feature 19): it gives the model room to answer (a reasoning model given 3 tokens
/// answered nothing, which read as "no" for every record), reads yes and no, and fails on anything else.
/// </summary>
public sealed class PackMeaningJudgeTests
{
    private sealed class Model(string answer) : IChatClient
    {
        public ChatOptions? Options { get; private set; }
        public string? Prompt { get; private set; }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options = options;
            Prompt = string.Join("\n", messages.Select(m => m.Text));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, answer)));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose() { }
    }

    [Theory]
    [InlineData("Yes.", true)]
    [InlineData("  yes", true)]
    [InlineData("No", false)]
    [InlineData("no, it does not", false)]
    public async Task The_judge_reads_yes_and_no(string answer, bool expected)
    {
        var model = new Model(answer);
        (await new ChatClientPackMeaningJudge(model).AnswersAsync("Can I bring my dog?", "Pets. Dogs stay in the office.")).Should().Be(expected);
        model.Prompt.Should().Be(IPackMeaningJudge.Prompt("Can I bring my dog?", "Pets. Dogs stay in the office."), "one prompt for every judge");
        model.Options!.MaxOutputTokens.Should().BeGreaterThanOrEqualTo(64, "a reasoning model spends its first tokens thinking");
        model.Options.Temperature.Should().Be(0);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Maybe")]
    public async Task An_answer_that_is_neither_yes_nor_no_is_a_failure_not_a_no(string answer) =>
        await FluentActions.Awaiting(() => new ChatClientPackMeaningJudge(new Model(answer)).AnswersAsync("q", "t"))
            .Should().ThrowAsync<InvalidOperationException>();
}
