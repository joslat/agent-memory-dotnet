// SPDX-License-Identifier: Apache-2.0
using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;
using AgentMemory.Abstractions.Options;
using AgentMemory.Extensibility.Capabilities;
using AgentMemory.Extensibility.Context;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;

namespace AgentMemory.Extensibility.Contributors;

/// <summary>Explicit contributor using the same declared-view evaluator as standalone validation and Workbench.</summary>
[Experimental("AMEXT001")]
public sealed class ViewContributor : IContextContributor
{
    private readonly IModuleViews _views;
    private readonly string _viewId;
    private readonly Func<ContextRequest, ModuleViewArguments?>? _arguments;
    private readonly bool _search;

    /// <summary>How long a search view waits for core's candidates before it searches with the question alone.</summary>
    internal static readonly TimeSpan CoreCandidatesWait = TimeSpan.FromSeconds(2);
    /// <inheritdoc/>
    public ContextContributorDescriptor Descriptor { get; }

    /// <summary>Binds a declared local view and read capability without issuing a query or registering services.</summary>
    public ViewContributor(ModuleResolution resolution, IModuleReader reader, string viewId, int priority = 100)
    {
        ArgumentNullException.ThrowIfNull(resolution); ArgumentNullException.ThrowIfNull(reader);
        _views = new ModuleViews(resolution, reader);
        if (!resolution.Modules.Single(m => m.Id == reader.CallerModuleId).Views.Any(v => v.Id == viewId))
            throw new ArgumentException("View must belong to the bound reader's module.", nameof(viewId));
        _viewId = viewId;
        _search = resolution.Modules.Single(m => m.Id == reader.CallerModuleId).Views.Single(v => v.Id == viewId).Search is not null;
        var id = ModuleIdentity.OwnedId(reader.CallerModuleId, viewId);
        Descriptor = new(id, new[] { id }.ToFrozenSet(StringComparer.Ordinal), priority);
    }

    /// <summary>Binds a cheap per-request argument selector. Null means this view is not applicable.</summary>
    /// <remarks>The selector runs once after owner policy resolution, never for unsupported valid time.
    /// It cannot override the declared view or the compiler's resolved owner/shared scope.</remarks>
    public ViewContributor(ModuleResolution resolution, IModuleReader reader, string viewId, int priority,
        Func<ContextRequest, ModuleViewArguments?> arguments) : this(resolution, reader, viewId, priority)
    {
        ArgumentNullException.ThrowIfNull(arguments); _arguments = arguments;
    }

    private static async Task<string> CoreTextAsync(ContextRequest request, CancellationToken cancellationToken)
    {
        if (request.CoreCandidates is not { } core) return "";
        try
        {
            var texts = await core.WaitAsync(CoreCandidatesWait, cancellationToken).ConfigureAwait(false);
            return texts.Count == 0 ? "" : " " + string.Join(" ", texts);
        }
        catch (TimeoutException) { return ""; }
    }

    /// <inheritdoc/>
    public ValueTask<bool> AppliesAsync(ContextRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        // A valid-time pin applies (feature 07): the read keeps the records true then.
        return ValueTask.FromResult(true);
    }

    /// <inheritdoc/>
    /// <remarks>Legacy section-only callers cannot inspect omission reasons; use ContributeResultAsync for diagnostics.</remarks>
    public async Task<ContextSection?> ContributeAsync(ContextRequest request, CancellationToken cancellationToken)
        => (await ContributeResultAsync(request, cancellationToken).ConfigureAwait(false)).Section;

    /// <inheritdoc/>
    public async Task<ContextContributionResult> ContributeResultAsync(ContextRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request); cancellationToken.ThrowIfCancellationRequested();
        // A valid-time pin reads records as true then (feature 07): the module set's reader keeps the records live at it.
        var arguments = _arguments?.Invoke(request);
        if (_arguments is not null && arguments is null)
            return new() { OmissionReason = ContextOmissionReason.NotApplicable, Detail = "No view arguments selected for this request." };
        // A search view searches with the turn's question unless a selector chose its own text (Extensibility 0.13 §K4), and
        // adds what core memory found for the turn after it (S2): the question's words come first within the term budget.
        if (_search && arguments?.Search is null)
        {
            if (string.IsNullOrWhiteSpace(request.Query))
                return new() { OmissionReason = ContextOmissionReason.NotApplicable, Detail = "No question to search with." };
            arguments = (arguments ?? new()) with
            {
                // By meaning, the question alone (feature 19): core's texts would pull the vector toward what core already found.
                Search = request.Query + await CoreTextAsync(request, cancellationToken).ConfigureAwait(false), Meaning = request.Query,
            };
        }
        var context = new ModuleReadContext
        {
            Scope = request.ResolvedScope ?? (request.Owner is null ? null : MemoryScope.For(request.Owner)),
            SessionId = request.SessionId, SystemAsOfUtc = request.SystemAsOf, ValidAsOfUtc = request.AsOf,
        };
        var result = arguments is null
            ? await _views.EvaluateAsync(_viewId, context, cancellationToken).ConfigureAwait(false)
            : await _views.EvaluateWithArgumentsAsync(_viewId, arguments, context, cancellationToken).ConfigureAwait(false);
        if (result.Status != ModuleReadStatus.Succeeded) return new()
        {
            OmissionReason = result.Status == ModuleReadStatus.NotApplicable ? ContextOmissionReason.NotApplicable : ContextOmissionReason.Denied,
            Detail = string.Join("; ", result.Errors)
        };
        if (result.Items.Count == 0) return new() { OmissionReason = ContextOmissionReason.SearchedAndEmpty };
        return new() { Section = new(Descriptor.Id, Descriptor.Id, 1,
            result.Items.Select(i => new ContextItem(i.Record.Id, i.Text)
            {
                Provenance = new()
                {
                    RecordModuleId = i.Record.ModuleId, RecordType = i.Record.Type,
                    RecordVersionId = i.Record.Id, RecordKey = i.Record.Key, ConfidenceMicro = i.Record.ConfidenceMicro,
                    CreatedAtUtc = i.Record.CreatedAtUtc, InvalidatedAtUtc = i.Record.InvalidatedAtUtc,
                    SourceSessionId = i.Record.SourceSessionId,
                    SourceMessageIds = Array.AsReadOnly(i.Record.Source.MessageIds.ToArray()),
                    SourcePack = i.Record.Source.Pack
                }
            }).ToList().AsReadOnly(), result) };
    }
}
