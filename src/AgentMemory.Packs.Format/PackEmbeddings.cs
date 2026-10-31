using System.Buffers.Binary;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AgentMemory.Packs.Format;

/// <summary>
/// The Embeddings profile (feature 03): vectors a pack carries, as signed content. Each set is two files under
/// <c>embeddings/</c>: <c>&lt;name&gt;.st</c> in the safetensors layout (an 8-byte little-endian header length, a JSON header,
/// then the raw data) holding exactly one tensor <c>vectors</c> of shape [rows, dimension]; and <c>&lt;name&gt;.json</c>, its index
/// in the JSON profile, naming the model, the number type, the dimension, whether the rows are normalized, the distance they
/// are meant for, and the record each row is. A verifier reads both strictly and checks each against the other and the pack's
/// records; a host reuses vectors only on an exact match of model, dimension, number type and normalization
/// (<see cref="Reusable"/>), and never lets them decide a walk or a verdict.
/// </summary>
internal static partial class PackEmbeddings
{
    internal const string Folder = "embeddings/";
    internal const string Format = "ampack.embeddings/1";
    /// <summary>The largest header a set may carry: one tensor's dtype, shape and offsets fit in far less.</summary>
    internal const int MaximumHeaderBytes = 64 * 1024;
    internal const int MaximumDimension = 4096;
    internal const int MaximumSets = 8;
    /// <summary>The most rows a set holds: one per record, and a pack holds at most 5,000.</summary>
    internal const int MaximumRows = 5000;

    /// <summary>One set's index, as read.</summary>
    internal sealed record Index(string Name, string Model, string? ModelDigest, string DataType, int Dimension, bool Normalized, string Distance,
        IReadOnlyList<(string Type, string Key)> Rows);

    /// <summary>What a host's own embedder is, for <see cref="Reusable"/>.</summary>
    internal sealed record Embedder(string Model, string? ModelDigest, int Dimension, string DataType, bool Normalized);

    /// <summary>
    /// Whether a host may use a set's vectors in its own retrieval: only on an exact match (R-EMB-010). Otherwise it embeds the
    /// records' text with its own model, and never mixes two models' vectors in one index.
    /// </summary>
    internal static bool Reusable(Index set, Embedder host) =>
        set.Model == host.Model && set.ModelDigest == host.ModelDigest && set.Dimension == host.Dimension
        && set.DataType == host.DataType && set.Normalized == host.Normalized;

    /// <summary>Every set the files hold, read and checked against each other and the records; or the first problem.</summary>
    internal static (IReadOnlyList<Index> Sets, string? Problem) Read(IReadOnlyDictionary<string, byte[]> files)
    {
        var paths = files.Keys.Where(p => p.StartsWith(Folder, StringComparison.Ordinal)).Order(StringComparer.Ordinal).ToList();
        if (paths.Count == 0) return ([], null);
        var names = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var path in paths)
        {
            var match = SetFile().Match(path);
            if (!match.Success) return ([], $"'{path}' is not an embeddings file: embeddings/<name>.st and embeddings/<name>.json, a name of 1 to 64 lowercase letters, digits or '-'.");
            names.Add(match.Groups[1].Value);
        }
        if (names.Count > MaximumSets) return ([], $"A pack carries at most {MaximumSets} embedding sets.");
        var records = Records(files);
        var sets = new List<Index>();
        foreach (var name in names)
        {
            if (!files.TryGetValue(Folder + name + ".st", out var tensor) || !files.TryGetValue(Folder + name + ".json", out var index))
                return ([], $"The embedding set '{name}' needs both embeddings/{name}.st and embeddings/{name}.json.");
            var (set, indexProblem) = ReadIndex(name, index);
            if (set is null) return ([], indexProblem);
            if (TensorProblem(set, tensor) is { } tensorProblem) return ([], tensorProblem);
            foreach (var row in set.Rows)
                if (!records.Contains(row)) return ([], $"embeddings/{name}.json names a row {row.Type} '{row.Key}' the pack holds no record of.");
            sets.Add(set);
        }
        return (sets, null);
    }

    private static (Index? Set, string? Problem) ReadIndex(string name, byte[] bytes)
    {
        var at = $"embeddings/{name}.json";
        if (PackJson.Check(bytes) is { } json) return (null, $"'{at}' {json}.");
        using var document = JsonDocument.Parse(bytes);
        var root = document.RootElement;
        string? Text(string field) => root.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(p => !PackRegistry.Shape("embeddingsIndex").Contains(p.Name)))
            return (null, $"{at} holds other fields than an embeddings index.");
        if (Text("format") != Format) return (null, $"{at} names format '{Format}'.");
        if (Text("model") is not { } model || !PackCard.IsText(model, 200) || model.Contains('\n') || model.Contains('\t'))
            return (null, $"{at} names its model as one line of 1 to 200 characters.");
        string? digest = null;
        if (root.TryGetProperty("modelDigest", out _) && (Text("modelDigest") is not { } d || !PackCard.IsText(d, 200) || d.Contains('\n') || d.Contains('\t')))
            return (null, $"{at} names its model's digest as one line of 1 to 200 characters.");
        else digest = Text("modelDigest");
        if (Text("dtype") is not { } dtype || !PackRegistry.EmbeddingDataTypes.Contains(dtype))
            return (null, $"{at} names a dtype of {string.Join(" or ", PackRegistry.EmbeddingDataTypes.Order(StringComparer.Ordinal))}.");
        if (!root.TryGetProperty("dimension", out var dim) || PackNumbers.Integer(dim) is not { } dimension || dimension is < 1 or > MaximumDimension)
            return (null, $"{at} names a dimension, an integer of 1 to {MaximumDimension}.");
        if (!root.TryGetProperty("normalized", out var norm) || norm.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return (null, $"{at} says whether its vectors are normalized, true or false.");
        if (Text("distance") is not { } distance || !PackRegistry.EmbeddingDistances.Contains(distance))
            return (null, $"{at} names a distance of {string.Join(", ", PackRegistry.EmbeddingDistances.Order(StringComparer.Ordinal))}.");
        if (!root.TryGetProperty("rows", out var rows) || rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() is 0 or > MaximumRows)
            return (null, $"{at} lists 1 to {MaximumRows} rows.");
        var list = new List<(string, string)>();
        var seen = new HashSet<(string, string)>();
        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 2 || row[0].ValueKind != JsonValueKind.String || row[1].ValueKind != JsonValueKind.String)
                return (null, $"{at}: each row is [type, key], two strings.");
            var pair = (row[0].GetString()!, row[1].GetString()!);
            if (!seen.Add(pair)) return (null, $"{at} lists {pair.Item1} '{pair.Item2}' twice.");
            list.Add(pair);
        }
        return (new Index(name, model, digest, dtype, (int)dimension, norm.GetBoolean(), distance, list), null);
    }

    // The safetensors layout, strictly: the header length, the header in the JSON profile (trailing spaces allowed, as the
    // layout pads), exactly one tensor `vectors` of the index's dtype and shape, offsets [0, its bytes], nothing after the
    // data, and no NaN or infinite value.
    private static string? TensorProblem(Index set, byte[] bytes)
    {
        var at = $"embeddings/{set.Name}.st";
        if (bytes.Length < 8) return $"{at} is shorter than its header length.";
        var length = BinaryPrimitives.ReadUInt64LittleEndian(bytes);
        if (length is 0 or > MaximumHeaderBytes || (ulong)bytes.Length < 8 + length) return $"{at} states a header of {length} bytes, past its bound or its end.";
        var header = bytes.AsSpan(8, (int)length);
        var trimmed = header.TrimEnd((byte)' ');
        if (PackJson.Check(trimmed.ToArray()) is { } json) return $"{at}'s header {json}.";
        using var document = JsonDocument.Parse(trimmed.ToArray());
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 1 || !root.TryGetProperty("vectors", out var tensor))
            return $"{at}'s header holds exactly one tensor, 'vectors', and no metadata.";
        if (tensor.ValueKind != JsonValueKind.Object || tensor.EnumerateObject().Any(p => p.Name is not ("dtype" or "shape" or "data_offsets")))
            return $"{at}'s tensor is exactly dtype, shape and data_offsets.";
        if (!tensor.TryGetProperty("dtype", out var dtype) || dtype.ValueKind != JsonValueKind.String || dtype.GetString() != set.DataType)
            return $"{at}'s tensor is not {set.DataType}, as its index says.";
        var size = set.DataType == "F32" ? 4 : 2;
        long expected = (long)set.Rows.Count * set.Dimension * size;
        if (!tensor.TryGetProperty("shape", out var shape) || shape.ValueKind != JsonValueKind.Array || shape.GetArrayLength() != 2
            || PackNumbers.Integer(shape[0]) != set.Rows.Count || PackNumbers.Integer(shape[1]) != set.Dimension)
            return $"{at}'s shape is not [{set.Rows.Count}, {set.Dimension}], its rows and dimension.";
        if (!tensor.TryGetProperty("data_offsets", out var offsets) || offsets.ValueKind != JsonValueKind.Array || offsets.GetArrayLength() != 2
            || PackNumbers.Integer(offsets[0]) != 0 || PackNumbers.Integer(offsets[1]) != expected)
            return $"{at}'s data offsets are not [0, {expected}].";
        if (bytes.LongLength != 8 + (long)length + expected) return $"{at} holds {bytes.LongLength - 8 - (long)length} data bytes; its tensor is {expected}.";
        var data = bytes.AsSpan(8 + (int)length);
        for (var i = 0; i < data.Length; i += size)
        {
            var finite = size == 4
                ? (BinaryPrimitives.ReadUInt32LittleEndian(data[i..]) & 0x7F80_0000) != 0x7F80_0000
                : (BinaryPrimitives.ReadUInt16LittleEndian(data[i..]) & 0x7C00) != 0x7C00;
            if (!finite) return $"{at} holds a NaN or infinite value at row {i / size / set.Dimension}.";
        }
        return null;
    }

    // Every (type, key) the record lines name, as the built-in binding reads them (one listed parse site).
    private static HashSet<(string, string)> Records(IReadOnlyDictionary<string, byte[]> files) =>
        [.. PackBuiltIns.Named(PackLayout.RecordFiles(files.Keys).Select(f => (ReadOnlyMemory<byte>)files[f]))];

    [GeneratedRegex(@"\Aembeddings/([a-z0-9][a-z0-9-]{0,63})\.(?:st|json)\z", RegexOptions.CultureInvariant)]
    private static partial Regex SetFile();
}
