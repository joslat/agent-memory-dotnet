using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using AgentMemory.Extensibility.Modules;

namespace AgentMemory.Extensibility.Reading;

/// <summary>Pure query validation and snapshotting; storage must separately resolve isolation and anchors.</summary>
[Experimental("AMEXT001")]
public static class ModuleQueryAdmission
{
    /// <summary>Checks calling-module access and view-compatible syntax, then captures typed substitutions.</summary>
    public static ModuleQueryAdmissionResult Admit(ModuleResolution resolution, string callerModuleId,
        ModuleQuery query, ModuleReadContext context, DateTimeOffset nowUtc)
    {
        ArgumentNullException.ThrowIfNull(resolution); ArgumentNullException.ThrowIfNull(query); ArgumentNullException.ThrowIfNull(context);
        var caller = resolution.Modules.SingleOrDefault(m => m.Id == callerModuleId);
        var target = resolution.Modules.SingleOrDefault(m => m.Id == query.ModuleId);
        if (!resolution.Succeeded || caller is null || target is null
            || (caller.Id != target.Id && !caller.Modules.ContainsKey(target.Id)))
            return Refused("Read target must be the bound calling module or a declared direct dependency.");
        if (context.ValidAsOfUtc is not null) return new() { Status = ModuleReadStatus.NotApplicable, Errors = ["as-of: valid time is unsupported for module records."] };
        if (query.Limit is <= 0 or > ModuleQuery.MaximumLimit || query.Key is not null && string.IsNullOrWhiteSpace(query.Key)
            || query.Where is null || query.Where.Count > 64 || query.Where.Any(f => f is null))
            return Refused("Query requires a positive bounded limit, nonempty optional key and at most 64 non-null filters.");
        // Reuse the manifest's typed view grammar. Frozen metadata is already valid; replacing its
        // views validates exactly the caller's query without creating a second operator/type policy.
        var errors = (target with { Views = [new() { Id = "query", Type = query.Type,
            Where = query.Where, OrderBy = query.OrderBy, Limit = query.Limit }] }).Validate();
        if (errors.Count != 0) return new() { Status = ModuleReadStatus.Refused, Errors = errors };
        try
        {
            var filters = query.Where.Select(f => f with { Value = Resolve(f.Value, context, nowUtc) }).ToList().AsReadOnly();
            return new() { Status = ModuleReadStatus.Succeeded, Query = query with { Where = filters, OrderBy = query.OrderBy is null ? null : query.OrderBy with { } } };
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        { return Refused("Filter cannot be captured: " + exception.Message); }
    }

    private static JsonElement Resolve(JsonElement value, ModuleReadContext context, DateTimeOffset nowUtc)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            if (value.GetArrayLength() > 256) throw new ArgumentException("Membership filters support at most 256 values.");
            return JsonSerializer.SerializeToElement(value.EnumerateArray().Select(v => Resolve(v, context, nowUtc)).ToArray());
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            if (value.GetString() == "$now") return JsonSerializer.SerializeToElement((context.SystemAsOfUtc ?? nowUtc).ToUniversalTime());
            if (value.GetString() == "$session") return !string.IsNullOrWhiteSpace(context.SessionId)
                ? JsonSerializer.SerializeToElement(context.SessionId) : throw new ArgumentException("A $session filter requires a session identity.");
        }
        return value.Clone();
    }
    private static ModuleQueryAdmissionResult Refused(string error) => new() { Status = ModuleReadStatus.Refused, Errors = [error] };
}

/// <summary>A frozen query with resolved placeholders, or a refusal without a query.</summary>
[Experimental("AMEXT001")]
public sealed record ModuleQueryAdmissionResult
{
    /// <summary>Admission disposition; success is not evidence that a database read occurred.</summary>
    public required ModuleReadStatus Status { get; init; }
    /// <summary>Independent admitted query, present only on success.</summary>
    public ModuleQuery? Query { get; init; }
    /// <summary>Concrete validation or applicability details.</summary>
    public IReadOnlyList<string> Errors { get; init; } = [];
}
