using AgentMemory.Extraction.Llm;
using FluentAssertions;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentMemory.Tests.Unit.Extraction;

/// <summary>
/// Unified extraction cannot honour the four per-kind prompt overrides, so it must REFUSE them rather
/// than ignore them.
/// </summary>
/// <remarks>
/// One unified prompt covers entities, facts, preferences and relations together; there is no coherent
/// way to apply four separate per-kind override prompts to a single call. That makes silent
/// ignoring the failure mode to prevent — a consumer who set <c>FactExtractionPrompt</c> and then
/// enabled unified extraction would get neither their prompt nor any indication it was dropped.
/// <para>
/// This guard is what makes flipping <c>UseUnifiedExtraction</c> to the shipped default safe: the one
/// override unified CAN honour, <see cref="Abstractions.Options.LlmExtractionOptions.EntityTypes"/>,
/// was wired up alongside it; the four it cannot are now loud.
/// </para>
/// </remarks>
public sealed class UnifiedExtractionOverrideRejectionTests
{
    private static IServiceProvider Build(Action<LlmExtractionOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IChatClient>(new ThrowingChatClient());
        services.AddLlmExtraction(configure);
        return services.BuildServiceProvider();
    }

    private static Action Resolve(Action<LlmExtractionOptions> configure) =>
        () => Build(configure).GetRequiredService<IOptions<LlmExtractionOptions>>().Value.ToString();

    [Theory]
    [InlineData("EntityExtractionPrompt")]
    [InlineData("FactExtractionPrompt")]
    [InlineData("RelationshipExtractionPrompt")]
    [InlineData("PreferenceExtractionPrompt")]
    public void EnablingUnifiedExtractionWithAPerKindPromptOverrideIsRejected(string property)
    {
        Resolve(o =>
        {
            o.UseUnifiedExtraction = true;
            typeof(LlmExtractionOptions).GetProperty(property)!.SetValue(o, "custom prompt");
        }).Should().Throw<OptionsValidationException>()
          .WithMessage("*" + property + "*");
    }

    [Fact]
    public void ThePerKindPathStillAcceptsEveryOverride()
    {
        // The overrides are not deprecated — they work exactly as before on the path that can express
        // them. Rejecting them wholesale would be a worse break than the one being prevented.
        Resolve(o =>
        {
            o.UseUnifiedExtraction = false;
            o.EntityExtractionPrompt = "custom";
            o.FactExtractionPrompt = "custom";
            o.RelationshipExtractionPrompt = "custom";
            o.PreferenceExtractionPrompt = "custom";
        }).Should().NotThrow();
    }

    [Fact]
    public void UnifiedExtractionWithCustomEntityTypesIsAccepted()
    {
        // EntityTypes is the override unified CAN honour, so it must not be swept up by the guard.
        Resolve(o =>
        {
            o.UseUnifiedExtraction = true;
            o.EntityTypes = new[] { "PRODUCT", "SKU" };
        }).Should().NotThrow();
    }

    [Fact]
    public void PlainUnifiedExtractionIsAccepted()
    {
        Resolve(o => o.UseUnifiedExtraction = true).Should().NotThrow();
    }

    private sealed class ThrowingChatClient : IChatClient
    {
        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Options validation must not call the model.");

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException("Options validation must not call the model.");

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
