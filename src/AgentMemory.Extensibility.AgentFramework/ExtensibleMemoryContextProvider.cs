using System.Diagnostics.CodeAnalysis;
using System.Text;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Security;
using AgentMemory.Abstractions.Domain;
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
    private readonly IMemoryContextAdmissionPolicy? _admissionPolicy;

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
        ILogger<ExtensibleMemoryContextProvider> logger,
        // EVERY OPTIONAL DEPENDENCY THE BASE TAKES, THREADED THROUGH.
        //
        // Omitting them does not fail to compile and does not fail a test that builds both providers
        // by hand -- it silently DEGRADES the provider a host actually resolves. Dropping
        // IMemoryStoreContext puts a multi-tenant host on the wrong store; dropping
        // IMemoryContextAdmissionPolicy removes the #92 gate from CORE memory, which is the opposite
        // of what this class claims to preserve; dropping MemoryToolFactory takes the agent's memory
        // tools away. "The extensible provider IS the shipped provider" is only true if it is
        // constructed like one.
        AgentMemory.Abstractions.Services.IMemoryStoreContext? storeContext = null,
        AgentMemory.Abstractions.Services.IWritableMemoryOwnerContext? ownerContext = null,
        AgentMemory.AgentFramework.Tools.MemoryToolFactory? toolFactory = null,
        AgentMemory.AgentFramework.Recall.IAutomaticRecallPolicy? recallPolicy = null,
        IMemoryContextAdmissionPolicy? admissionPolicy = null)
        : base(memoryService, embeddingOrchestrator, clock, idGenerator, memoryOptions, formatOptions,
               agentOptions, baseLogger, storeContext, ownerContext, toolFactory, recallPolicy,
               admissionPolicy)
    {
        ArgumentNullException.ThrowIfNull(moduleContributors);
        _compiler = compiler ?? throw new ArgumentNullException(nameof(compiler));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _admissionPolicy = admissionPolicy;

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
        var moduleText = RenderModuleSections(envelope, _admissionPolicy, _logger);

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

            // THE SAME IDENTITY THE BASE USES, from the base. ExtractIds is protected precisely so
            // this cannot become a second derivation: a module that retrieved against a different
            // session id than core did would be worse than one that retrieved nothing, because the
            // context would look complete.
            var ids = ExtractIds(context.Session, context.Agent);

            var request = new ContextRequest
            {
                SessionId = ids.sessionId,
                ConversationId = ids.conversationId,
                Owner = ids.userId,
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
    private static string RenderModuleSections(
        ContextEnvelope? envelope,
        IMemoryContextAdmissionPolicy? admissionPolicy,
        ILogger logger)
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

            // MODULE TEXT CROSSES THE SAME TRUST BOUNDARY AS RECALLED MEMORY, and until now it
            // crossed it unchecked. A module section is third-party content going into the
            // instruction block -- the exact surface #92 spent eight phases gating -- and a module
            // reading an external store is a longer reach than recall has. Every item is put through
            // the same admission policy recalled memory is, and a refused item is dropped and logged
            // rather than trimmed or silently admitted.
            var admitted = new List<ContextItem>(section.Items.Count);
            foreach (var item in section.Items)
            {
                if (admissionPolicy is null)
                {
                    admitted.Add(item);
                    continue;
                }

                var decision = admissionPolicy.Evaluate(new MemoryAdmissionContext
                {
                    Category = section.TypeId,
                    Content = item.Text,
                    TrustLevel = MemoryTrustLevel.Untrusted,
                });

                if (decision.Include)
                {
                    admitted.Add(item);
                    continue;
                }

                logger.LogWarning(
                    "Module section '{SectionType}' item refused by the admission policy: {Reason}",
                    section.TypeId, decision.ExclusionReason ?? "instruction-like content");
            }

            // A section whose every item was refused contributes no header either: a heading with
            // nothing under it tells the model a section exists and says nothing about it.
            if (admitted.Count == 0) continue;

            if (builder.Length > 0) builder.AppendLine().AppendLine();
            builder.Append("### ").AppendLine(section.TypeId);
            foreach (var item in admitted) builder.AppendLine(item.Text);
        }

        return builder.ToString().TrimEnd();
    }
}
