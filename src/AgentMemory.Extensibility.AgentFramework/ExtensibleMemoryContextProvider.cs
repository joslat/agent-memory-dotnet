using System.Diagnostics.CodeAnalysis;
using System.Text;
using AgentMemory.AgentFramework;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Contributors;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace AgentMemory.Extensibility.AgentFramework;

/// <summary>
/// An <see cref="AIContextProvider"/> that composes the shipped one and appends module sections.
/// </summary>
/// <remarks>
/// <para>
/// <b>Delegation, not re-rendering, and that is the entire safety property.</b> The core block comes
/// from calling the shipped <see cref="Neo4jMemoryContextProvider"/> with the unmodified invoking
/// context, so the #92 admission gate, the recalled-role gate, history de-duplication and the delta
/// checkpoint are the existing code paths, byte for byte. With no modules registered this provider's
/// output is identical to that provider's <b>by construction</b> rather than by comparison — there is
/// no second rendering path that could drift, because there is no second rendering path.
/// </para>
/// <para>
/// The alternative — re-rendering core sections through the formatter — was rejected for two reasons
/// that are not about access. The 1.0 lockdown internalised the formatter deliberately, and anything
/// made public is SemVer-locked from the day it ships; and the facade's own comment records that a
/// second rendering path is where the admission gate drifts. Typed core sections, if a renderer ever
/// needs them, arrive behind ONE new seam rather than by publishing three classes.
/// </para>
/// <para>
/// <b>The composed provider keeps its own state key.</b> It is a separate provider instance with its
/// own bag entry; this one writes nothing under <c>"Neo4jMemory"</c>, so the after-run correlation the
/// shipped provider does with itself is untouched.
/// </para>
/// </remarks>
[Experimental("AMEXT001")]
public sealed class ExtensibleMemoryContextProvider : Neo4jMemoryContextProvider
{
    private readonly IContextCompiler _compiler;
    private readonly ILogger<ExtensibleMemoryContextProvider> _logger;
    private readonly bool _hasModuleContributors;

    /// <summary>Creates the provider. Base dependencies are the shipped provider's, unchanged.</summary>
    public ExtensibleMemoryContextProvider(
        AgentMemory.Abstractions.Services.IMemoryService memoryService,
        AgentMemory.Abstractions.Services.IEmbeddingOrchestrator embeddingOrchestrator,
        AgentMemory.Abstractions.Services.IClock clock,
        AgentMemory.Abstractions.Services.IIdGenerator idGenerator,
        Microsoft.Extensions.Options.IOptions<AgentMemory.Abstractions.Options.MemoryOptions> memoryOptions,
        Microsoft.Extensions.Options.IOptions<ContextFormatOptions> formatOptions,
        Microsoft.Extensions.Options.IOptions<AgentFrameworkOptions> agentOptions,
        ILogger<Neo4jMemoryContextProvider> baseLogger,
        IContextCompiler compiler,
        IEnumerable<Capabilities.IContextContributor> moduleContributors,
        ILogger<ExtensibleMemoryContextProvider> logger)
        : base(memoryService, embeddingOrchestrator, clock, idGenerator, memoryOptions, formatOptions,
               agentOptions, baseLogger)
    {
        ArgumentNullException.ThrowIfNull(moduleContributors);
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The CORE contributor is not a module. It exists for hosts that compile context instead of
        // inheriting it; if its presence flipped this provider into compile-and-append, core memory
        // would be rendered twice -- once by the base, once by the compiler.
        _hasModuleContributors = moduleContributors.Any(c => !string.Equals(
            c.Descriptor.Id, CoreMemoryContextContributor.ContributorId, StringComparison.Ordinal));
    }

    // NO SECOND STATE KEY. Inheriting means there is one provider instance, so it keeps the shipped
    // `"Neo4jMemory"` key and the after-run correlation the base already does with itself is
    // untouched. A separate key would have been right for composition and is wrong here -- it would
    // describe a second participant that does not exist.

    /// <inheritdoc/>
    protected override async ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        // THE CORE BLOCK, FROM THE CODE THAT ALREADY RENDERS IT, unmodified.
        //
        // `base.`, not a composed instance's public InvokingAsync: that wrapper stamps attribution
        // and merges the turn, so composing applied it twice and the result was measurably not
        // byte-identical. One instance, one stamp.
        var coreContext = await base.ProvideAIContextAsync(context, cancellationToken).ConfigureAwait(false);

        // NOTHING TO COMPILE WITHOUT MODULES. The core block is delegated, so the compiler here only
        // ever runs module contributors; with none registered there is no work, no identity to
        // resolve, and no opportunity to diverge from the composed provider.
        if (!_hasModuleContributors) return coreContext;

        var envelope = await CompileModulesAsync(context, cancellationToken).ConfigureAwait(false);
        var moduleText = RenderModuleSections(envelope);

        // NOTHING APPENDED MEANS NOTHING CHANGED. Returning the core AIContext itself, rather than a
        // copy with equal fields, is what makes the empty-module case identical instead of merely
        // equivalent -- and the parity test asserts that distinction.
        if (moduleText.Length == 0) return coreContext;

        return new AIContext
        {
            Instructions = string.IsNullOrEmpty(coreContext.Instructions)
                ? moduleText
                : coreContext.Instructions + Environment.NewLine + Environment.NewLine + moduleText,
            Messages = coreContext.Messages,
            Tools = coreContext.Tools,
        };
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Straight through to the composed provider: persistence, extraction and its exception handling
    /// are exactly today's. Module ingestion proposals are a later slice and are deliberately absent
    /// rather than stubbed — a write path that looked registered and did nothing would be worse than
    /// its absence.
    /// </remarks>
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        return base.StoreAIContextAsync(context, cancellationToken);
    }

    private async Task<ContextEnvelope?> CompileModulesAsync(
        InvokingContext context, CancellationToken cancellationToken)
    {
        try
        {
            var turn = context.AIContext?.Messages?.ToArray() ?? [];

            var request = new ContextRequest
            {
                // SLICE A CARRIES NO RESOLVED IDS, and says so rather than guessing them.
                //
                // The shipped provider reads session, conversation, user and application through its
                // own private ExtractIds. Re-deriving them here would be a second identity path, and a
                // second path is precisely what this provider exists to avoid — it is the same
                // argument that made the core block delegated rather than re-rendered.
                //
                // It costs nothing today because core comes from delegation and no module exists to
                // need ids. The seam that shares ExtractIds belongs with the slice that ships a module
                // and can test it; inventing a SessionId now would put a wrong value on the wire and
                // make the wrongness invisible.
                SessionId = string.Empty,
                RecentTurn = turn,
                Query = turn.LastOrDefault()?.Text,
            };

            return await _compiler.CompileAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A module failure must never cost the host its core memory, which has already been
            // obtained above. The turn proceeds with core context alone.
            _logger.LogWarning(exception, "Module context compilation failed; core context is unaffected.");
            return null;
        }
    }

    /// <summary>
    /// Renders module sections only. The core section is opaque and is never rendered here.
    /// </summary>
    private static string RenderModuleSections(ContextEnvelope? envelope)
    {
        if (envelope is null) return string.Empty;

        var builder = new StringBuilder();
        foreach (var section in envelope.Sections)
        {
            // The core section carries the MemoryContext as metadata for correlation and carries no
            // text. Rendering it here would be the second rendering path this design exists to avoid.
            if (string.Equals(section.TypeId, CoreMemoryContextContributor.SectionType, StringComparison.Ordinal))
                continue;

            if (section.Items.Count == 0) continue;

            if (builder.Length > 0) builder.AppendLine().AppendLine();
            builder.Append("### ").AppendLine(section.TypeId);
            foreach (var item in section.Items) builder.AppendLine(item.Text);
        }

        return builder.ToString().TrimEnd();
    }
}
