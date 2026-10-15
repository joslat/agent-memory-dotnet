using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Reading;

/// <summary>One metadata-driven view implementation over an explicitly bound module reader.</summary>
/// <remarks>No database, provider or schema work occurs during construction. This service is not auto-registered.</remarks>
[Experimental("AMEXT001")]
public sealed class ModuleViews : IModuleViews
{
    private readonly ModuleManifest _module;
    private readonly IModuleReader _reader;

    /// <summary>Binds views to an already resolved module and its host-granted read capability.</summary>
    public ModuleViews(ModuleResolution resolution, IModuleReader reader)
    {
        ArgumentNullException.ThrowIfNull(resolution); ArgumentNullException.ThrowIfNull(reader);
        if (!resolution.Succeeded) throw new ArgumentException("Views require a successful module resolution.", nameof(resolution));
        _module = resolution.Modules.SingleOrDefault(m => m.Id == reader.CallerModuleId)
            ?? throw new ArgumentException("The reader's module is not resolved.", nameof(reader));
        _reader = reader;
    }

    /// <inheritdoc/>
    public Task<ModuleViewResult> EvaluateAsync(string viewId, ModuleReadContext context, CancellationToken cancellationToken = default) =>
        EvaluateWithArgumentsAsync(viewId, new(), context, cancellationToken);

    /// <inheritdoc/>
    public async Task<ModuleViewResult> EvaluateWithArgumentsAsync(string viewId, ModuleViewArguments arguments,
        ModuleReadContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(arguments); ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();
        var view = _module.Views.SingleOrDefault(v => v.Id == viewId);
        if (view is null) return new() { Status = ModuleReadStatus.Refused, Errors = ["Unknown view in the bound module."] };
        if (arguments.Key is not null && string.IsNullOrWhiteSpace(arguments.Key)
            || arguments.Limit is { } limit && (limit <= 0 || limit > view.Limit))
            return new() { Status = ModuleReadStatus.Refused, Errors = ["View arguments require a nonempty optional key and a positive limit no greater than the declared limit."] };
        if (view.Selection == ModuleRecordSelection.History && arguments.Key is null)
            return new() { Status = ModuleReadStatus.Refused, Errors = ["History views require one exact logical key."] };
        IReadOnlyList<ModuleLinkFilter> links;
        try { links = ModuleQueryAdmission.CaptureLinks(_module, view.Type, arguments.Links); }
        catch (ArgumentException exception) { return new() { Status = ModuleReadStatus.Refused, Errors = [exception.Message] }; }
        var read = await _reader.FindAsync(new() { ModuleId = _module.Id, Type = view.Type, Selection = view.Selection, Key = arguments.Key, Links = links,
            Where = view.Where, OrderBy = view.OrderBy, Limit = arguments.Limit ?? view.Limit }, context, cancellationToken).ConfigureAwait(false);
        if (read.Status != ModuleReadStatus.Succeeded) return new() { Status = read.Status, Errors = read.Errors };
        var type = _module.Ontology!.Types.Single(t => t.Name == view.Type);
        var items = new List<ModuleViewItem>();
        try
        {
            foreach (var record in read.Records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(new() { Record = record, Text = ModuleRenderTemplate.Render(type, record.Properties) });
            }
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or OverflowException)
        { return new() { Status = ModuleReadStatus.Refused, Errors = ["View rendering failed: " + exception.Message] }; }
        return new() { Status = ModuleReadStatus.Succeeded, Items = items.AsReadOnly() };
    }
}
