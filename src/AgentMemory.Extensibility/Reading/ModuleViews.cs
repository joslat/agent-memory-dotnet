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
    public async Task<ModuleViewResult> EvaluateAsync(string viewId, ModuleReadContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context); cancellationToken.ThrowIfCancellationRequested();
        var view = _module.Views.SingleOrDefault(v => v.Id == viewId);
        if (view is null) return new() { Status = ModuleReadStatus.Refused, Errors = ["Unknown view in the bound module."] };
        var read = await _reader.FindAsync(new() { ModuleId = _module.Id, Type = view.Type,
            Where = view.Where, OrderBy = view.OrderBy, Limit = view.Limit }, context, cancellationToken).ConfigureAwait(false);
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
