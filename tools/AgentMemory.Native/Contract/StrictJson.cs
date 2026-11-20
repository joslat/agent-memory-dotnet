using System.Text.Json;

namespace AgentMemory.Native.Contract;

/// <summary>A configuration or request the contract refuses; <see cref="Code"/> is stable, the message readable.</summary>
internal sealed class ContractException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Reads one JSON object of the native contract strictly: every property must be known, of the right kind and within
/// its bounds, and anything left unread is refused. Repeated property names are refused by the parser itself.
/// </summary>
internal sealed class StrictObject
{
    public const int MaxDocumentBytes = 64 * 1024;

    private static readonly JsonDocumentOptions Options = new()
    {
        AllowDuplicateProperties = false,
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 16,
    };

    private readonly JsonElement _element;
    private readonly string _path;
    private readonly HashSet<string> _read = new(StringComparer.Ordinal);

    private StrictObject(JsonElement element, string path)
    {
        _element = element;
        _path = path;
    }

    /// <summary>Parses a whole document; the caller disposes the returned document after reading.</summary>
    public static (JsonDocument Document, StrictObject Root) Parse(ReadOnlyMemory<byte> utf8, string what)
    {
        if (utf8.Length > MaxDocumentBytes)
            throw new ContractException("too_large", $"The {what} is {utf8.Length} bytes; the limit is {MaxDocumentBytes}.");
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8, Options);
        }
        catch (JsonException exception)
        {
            throw new ContractException("malformed_json", $"The {what} is not valid UTF-8 JSON: {exception.Message}");
        }
        catch (ArgumentException exception)
        {
            throw new ContractException("malformed_json", $"The {what} is not valid UTF-8 JSON: {exception.Message}");
        }
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            document.Dispose();
            throw new ContractException("not_an_object", $"The {what} must be one JSON object.");
        }
        return (document, new StrictObject(document.RootElement, "$"));
    }

    private bool TryGet(string name, out JsonElement value)
    {
        _read.Add(name);
        return _element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;
    }

    private ContractException Wrong(string name, string expectation) =>
        new("invalid_value", $"{_path}.{name} must be {expectation}.");

    public string RequiredString(string name, int maxLength = 4096) =>
        OptionalString(name, maxLength) ?? throw new ContractException("missing_property", $"{_path}.{name} is required.");

    public string? OptionalString(string name, int maxLength = 4096)
    {
        if (!TryGet(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw Wrong(name, "a string");
        var text = value.GetString()!;
        if (text.Length == 0 || text.Length > maxLength) throw Wrong(name, $"a non-empty string of at most {maxLength} characters");
        return text;
    }

    public int OptionalInt(string name, int fallback, int min, int max)
    {
        if (!TryGet(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < min || number > max)
            throw Wrong(name, $"a whole number from {min} to {max}");
        return number;
    }

    public double OptionalDouble(string name, double fallback, double min, double max)
    {
        if (!TryGet(name, out var value)) return fallback;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || !double.IsFinite(number)
            || number < min || number > max)
            throw Wrong(name, $"a number from {min} to {max}");
        return number;
    }

    public bool OptionalBool(string name, bool fallback)
    {
        if (!TryGet(name, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw Wrong(name, "true or false"),
        };
    }

    public StrictObject RequiredObject(string name) =>
        OptionalObject(name) ?? throw new ContractException("missing_property", $"{_path}.{name} is required.");

    public StrictObject? OptionalObject(string name)
    {
        if (!TryGet(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object) throw Wrong(name, "an object");
        return new StrictObject(value, $"{_path}.{name}");
    }

    public float[] RequiredVector(string name, int length)
    {
        if (!TryGet(name, out var value))
            throw new ContractException("missing_property", $"{_path}.{name} is required.");
        if (value.ValueKind != JsonValueKind.Array) throw Wrong(name, "an array of numbers");
        var count = value.GetArrayLength();
        if (count != length)
            throw new ContractException("dimension_mismatch", $"{_path}.{name} has {count} numbers; the store's vectors have {length}.");
        var vector = new float[count];
        var i = 0;
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number || !item.TryGetSingle(out var number) || !float.IsFinite(number))
                throw Wrong(name, "an array of finite numbers");
            vector[i++] = number;
        }
        return vector;
    }

    /// <summary>Refuses every property of this object that no reader asked for.</summary>
    public void EnsureNoUnknown()
    {
        foreach (var property in _element.EnumerateObject())
        {
            if (!_read.Contains(property.Name))
                throw new ContractException("unknown_property", $"{_path}.{property.Name} is not part of the contract.");
        }
    }
}
