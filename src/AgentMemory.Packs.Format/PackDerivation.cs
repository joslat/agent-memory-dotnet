// SPDX-License-Identifier: Apache-2.0
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>One input a pack was made from (feature 09): its digest, a name, its licence, and how far the publisher trusts it.</summary>
/// <param name="Digest"><c>sha256:</c> and 64 lowercase hexadecimal characters.</param>
/// <param name="Name">What it is called, the key a record's <c>sourceKey</c> names.</param>
/// <param name="Licence">Its licence, as text.</param>
/// <param name="ConfidenceMicro">How far it is trusted, in millionths: a record from it is trusted no further.</param>
internal sealed record PackDerivationInput(string Digest, string Name, string? Licence, int? ConfidenceMicro);

/// <summary>
/// What a pack was made from (feature 09, R-CORE-136): <c>META/derivation.json</c>, inside the signed content so it is part of the
/// pack's identity and cannot be swapped. Its inputs, the tool, the model and a digest of its prompt, the parameters, the parent
/// pack it was derived from, and what was done. Canonical (RFC 8785) and strict, read alike by both verifiers. A parent is
/// information, never trust: a derived pack is admitted on its own signatures and trial.
/// </summary>
internal sealed partial record PackDerivation(IReadOnlyList<PackDerivationInput> Inputs, string ToolName, string ToolVersion, string? ModelId,
    string? ModelVersion, string? PromptDigest, IReadOnlyDictionary<string, string> Parameters, string? ParentId, string? ParentDigest,
    IReadOnlyList<string> Actions)
{
    internal static readonly string FileName = PackRegistry.DerivationFile;
    internal const int MaximumInputs = 256;
    internal const int MaximumParameters = 64;
    internal const int MaximumConfidenceMicro = 1_000_000;

    /// <summary>The input a record's <c>sourceKey</c> names, if any.</summary>
    internal PackDerivationInput? Input(string name) => Inputs.FirstOrDefault(i => i.Name == name);

    /// <summary>Reads a derivation strictly, or returns why it is not one.</summary>
    internal static (PackDerivation? Derivation, string? Problem) Parse(byte[] bytes)
    {
        if (bytes.Length > PackVerifier.MaximumJsonBytes) return (null, $"{FileName} is larger than {PackVerifier.MaximumJsonBytes >> 20} MiB.");
        if (PackJson.Check(bytes, integersOnly: true) is { } malformed) return (null, $"{FileName} {malformed}.");
        // Canonical, so a number has one spelling for both verifiers (no -0, no exponent, no fraction).
        if (!Jcs.Canonical(bytes).AsSpan().SequenceEqual(bytes)) return (null, $"{FileName} is not canonical (RFC 8785).");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !PackRegistry.Shape("derivation").Contains(p.Name)))
            return (null, $"{FileName} holds other fields than a derivation.");
        if (Str(root, "format") != PackRegistry.DerivationFormat) return (null, $"{FileName} must name format '{PackRegistry.DerivationFormat}'.");
        if (!root.TryGetProperty("inputs", out var inputs) || inputs.ValueKind != JsonValueKind.Array || inputs.GetArrayLength() is 0 or > MaximumInputs)
            return (null, $"{FileName} names 1 to {MaximumInputs} inputs.");
        var read = new List<PackDerivationInput>();
        foreach (var input in inputs.EnumerateArray())
        {
            if (input.ValueKind != JsonValueKind.Object || input.EnumerateObject().Any(p => !PackRegistry.Shape("derivationInput").Contains(p.Name))
                || Str(input, "digest") is not { } digest || !IsDigest(digest) || Str(input, "name") is not { } name || !PackCard.IsLine(name, 256)
                || Optional(input, "licence", 128) is not { } licence || Micro(input) is not { } confidence)
                return (null, $"{FileName}: an input is a digest, a name of 1 to 256 characters, and optionally a licence and a confidenceMicro of 0 to {MaximumConfidenceMicro}.");
            if (read.Any(i => i.Name == name)) return (null, $"{FileName} names the input '{name}' twice.");
            read.Add(new PackDerivationInput(digest, name, licence.Value, confidence.Value));
        }
        if (!Pair(root, "tool", "derivationTool", "name", "version", required: true, out var tool))
            return (null, $"{FileName} names its tool as a name and a version, each 1 to 128 characters.");
        if (!Pair(root, "model", "derivationModel", "id", "version", required: false, out var model, secondRequired: false))
            return (null, $"{FileName} names its model as an id and, optionally, a version.");
        if (Optional(root, "promptDigest", 0) is not { } prompt || prompt.Value is { } p && !IsDigest(p))
            return (null, $"{FileName}: promptDigest is sha256: and 64 lowercase hexadecimal characters.");
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (root.TryGetProperty("parameters", out var given))
        {
            if (given.ValueKind != JsonValueKind.Object || given.EnumerateObject().Count() > MaximumParameters
                || given.EnumerateObject().Any(m => !PackCard.IsLine(m.Name, 128) || m.Value.ValueKind != JsonValueKind.String || !PackCard.IsLine(m.Value.GetString()!, 1024)))
                return (null, $"{FileName}: parameters is an object of at most {MaximumParameters} texts.");
            foreach (var member in given.EnumerateObject()) parameters[member.Name] = member.Value.GetString()!;
        }
        if (!Pair(root, "parent", "derivationParent", "id", "digest", required: false, out var parent)
            || parent is ({ } parentId, { } parentDigest) && (!PackManifest.IsValidId(parentId) || !IsDigest(parentDigest)))
            return (null, $"{FileName} names its parent as a pack id and the digest of its file list.");
        if (!root.TryGetProperty("actions", out var actions) || actions.ValueKind != JsonValueKind.Array || actions.GetArrayLength() == 0
            || actions.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String || !PackRegistry.DerivationActions.Contains(a.GetString()!))
            || actions.EnumerateArray().Select(a => a.GetString()).Distinct(StringComparer.Ordinal).Count() != actions.GetArrayLength())
            return (null, $"{FileName}: actions are distinct, of {string.Join(", ", PackRegistry.DerivationActions.Order(StringComparer.Ordinal))}.");
        return (new PackDerivation(read, tool.First!, tool.Second!, model.First, model.Second, prompt.Value, parameters, parent.First, parent.Second,
            [.. actions.EnumerateArray().Select(a => a.GetString()!)]), null);
    }

    [GeneratedRegex(@"\Asha256:[0-9a-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Digest();

    private static bool IsDigest(string text) => Digest().IsMatch(text);

    // Absent is (null); a text of 1 to maximum characters on one line is itself; anything else is null. With maximum 0, any string.
    private static Box<string?>? Optional(JsonElement element, string name, int maximum)
    {
        if (!element.TryGetProperty(name, out var value)) return new(null);
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString()!;
        return maximum == 0 || PackCard.IsLine(text, maximum) ? new(text) : null;
    }

    private static Box<int?>? Micro(JsonElement input)
    {
        if (!input.TryGetProperty("confidenceMicro", out var value)) return new(null);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var micro) && micro is >= 0 and <= MaximumConfidenceMicro ? new((int)micro) : null;
    }

    private static bool Pair(JsonElement root, string name, string shape, string first, string second, bool required, out (string? First, string? Second) pair,
        bool secondRequired = true)
    {
        pair = (null, null);
        if (!root.TryGetProperty(name, out var value)) return !required;
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(p => !PackRegistry.Shape(shape).Contains(p.Name))) return false;
        var a = Optional(value, first, first == "id" && name == "parent" ? 0 : name == "model" ? 256 : 128);
        var b = Optional(value, second, second == "digest" ? 0 : 128);
        if (a?.Value is null || b is null || secondRequired && b.Value is null) return false;
        pair = (a.Value, b.Value);
        return true;
    }

    private sealed record Box<T>(T Value);

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
