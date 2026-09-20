using System.Diagnostics.CodeAnalysis;
using System.Text;
using AgentMemory.AgentFramework;
using AgentMemory.AgentFramework.Mapping;
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

    // The one id the module compilation path never runs. Static because it never varies, and named
    // because "the core contributor" appears in three decisions in this file and must mean one thing.
    private static readonly IReadOnlySet<string> CoreContributorExclusion =
        new HashSet<string>(StringComparer.Ordinal) { CoreMemoryContextContributor.ContributorId };

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
        // THE BASE'S EFFECTIVE POLICY, not the constructor argument. They differ precisely when no
        // policy was supplied: the base substitutes a default and this class used to keep the null,
        // so a directly constructed provider gated CORE memory and admitted MODULE text unchecked --
        // the #92 gate missing from the least trusted surface in the class, which is the one place it
        // is least affordable. Reading it from the base is also why there is no second default here.
        var moduleText = RenderModuleSections(envelope, AdmissionPolicy, FormatOptions, _logger);

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

            // THE TENANT'S STORE SCOPE, REOPENED. The base opens one for the duration of its own
            // method, so by the time this runs it has already been disposed -- module contributors
            // would resolve the ambient store context and get the DEFAULT store while the core block
            // beside them came from the tenant's. Nothing would fail; a multi-tenant host would just
            // be served another tenant's default, which is the worst way for this to be wrong.
            // Threading IMemoryStoreContext into the constructor was necessary and not sufficient:
            // holding the dependency is not the same as being inside the scope.
            using var storeScope = ApplyStoreContext(ids.applicationId);

            var request = new ContextRequest
            {
                SessionId = ids.sessionId,
                ConversationId = ids.conversationId,
                Owner = ids.userId,
                RecentTurn = turn,
                Query = turn.LastOrDefault()?.Text,
            };

            // CORE IS EXCLUDED BECAUSE IT WAS ALREADY RENDERED, by `base` at the top of the turn.
            // A host that also calls AddCoreMemoryContributor() -- supported, and the natural thing
            // to do after reading the SDK docs -- puts the core contributor in the same container
            // this compiler reads. Without the exclusion it would run a SECOND full assembly, a live
            // query and an embedding call per turn, and the section would then be dropped by
            // RenderModuleSections: paid work, discarded, with nothing in any log to show for it.
            return await _compiler
                .CompileAsync(request, CoreContributorExclusion, cancellationToken)
                .ConfigureAwait(false);
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
        IMemoryContextAdmissionPolicy admissionPolicy,
        ContextFormatOptions formatOptions,
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
            // THE SAME ADMISSION FUNCTION RECALLED MEMORY GOES THROUGH, not a second one shaped like
            // it. The hand-rolled loop that stood here built its MemoryAdmissionContext without
            // `Mode` or `MinimumTrustForAdmissionBypass`, so a host running SecurityMode=Strict had
            // it honoured for core memory and silently ignored for module text -- the setting whose
            // entire purpose is to exclude instruction-like content, not reaching the newest and
            // least trusted surface in the provider. Calling AdmitItem makes that class of omission
            // impossible rather than fixed: there is one place the options are read.
            //
            // Its log lines say "recalled memory item"; the category is the module's section type, so
            // the entry is still unambiguous, and sharing the security decision is worth more than
            // the wording. Untrusted is not a guess -- module content is third-party by definition.
            var admitted = new List<ContextItem>(section.Items.Count);
            foreach (var item in section.Items)
            {
                if (MafTypeMapper.AdmitItem(
                        section.TypeId, item.Text, MemoryTrustLevel.Untrusted,
                        formatOptions, admissionPolicy, logger))
                {
                    admitted.Add(item);
                }
            }

            // A section whose every item was refused contributes no header either: a heading with
            // nothing under it tells the model a section exists and says nothing about it.
            if (admitted.Count == 0) continue;

            // DELIMITED AND ESCAPED, exactly as recalled memory is (#92 Phase 1). Admission alone is
            // not the boundary: in Permissive -- the DEFAULT -- instruction-like content is admitted
            // and flagged, and it is the delimiter that makes that safe, because the wrapper escapes
            // `<` and `>` so a module cannot close the block early or forge one of its own. Appending
            // the raw text, as this did, meant the newest untrusted surface in the provider was the
            // only one reaching the prompt undelimited.
            //
            // One wrapped block per SECTION, not per item, which is the shape recalled memory uses:
            // it joins a category's texts and wraps once. The category attribute carries the section
            // type, so the plain "### <type>" heading that stood here is gone rather than kept
            // alongside it -- two ways of naming the same thing is how a second rendering path starts.
            if (builder.Length > 0) builder.AppendLine().AppendLine();
            builder.Append(MafTypeMapper.WrapUntrustedContent(
                section.TypeId,
                string.Join(Environment.NewLine, admitted.Select(i => i.Text))));
        }

        return builder.ToString().TrimEnd();
    }
}
