using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace AgentMemory.Extensibility.Modules;

/// <summary>Module and owned-name syntax. Publisher authorization is a separate trust decision.</summary>
[Experimental("AMEXT001")]
public static class ModuleIdentity
{
    private static readonly Regex ModulePattern = new(@"\A[a-z][a-z0-9-]*(\.[a-z][a-z0-9-]*){2,}\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private static readonly Regex LocalPattern = new(@"\A[A-Za-z][A-Za-z0-9._-]*\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    /// <summary>Accepts lowercase dotted module IDs with at least three segments, at most 64 characters.</summary>
    public static bool IsValidModuleId(string? value) => value is { Length: > 0 and <= 64 } && ModulePattern.IsMatch(value);

    /// <summary>Identifies this project's reserved prefix without granting permission to use it.</summary>
    public static bool IsReservedModuleId(string? value) => IsValidModuleId(value)
        && value!.StartsWith("io.agentmemory.", StringComparison.Ordinal);

    /// <summary>Creates moduleId:local; local identifiers use ASCII letters, digits, dot, underscore or hyphen.</summary>
    public static string OwnedId(string moduleId, string local)
    {
        if (!IsValidModuleId(moduleId)) throw new ArgumentException("Invalid module ID.", nameof(moduleId));
        if (!IsValidLocalId(local)) throw new ArgumentException("Invalid local module identifier.", nameof(local));
        return moduleId + ":" + local;
    }

    /// <summary>Checks both ownership and identifier syntax, without accepting another namespace.</summary>
    public static bool IsOwnedId(string moduleId, string? value)
    {
        if (!IsValidModuleId(moduleId) || value is null || !value.StartsWith(moduleId + ":", StringComparison.Ordinal))
            return false;
        return IsValidLocalId(value[(moduleId.Length + 1)..]);
    }

    internal static bool IsValidLocalId(string? value) => value is { Length: > 0 and <= 64 } && LocalPattern.IsMatch(value);
}
