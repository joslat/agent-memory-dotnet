using System.Text;
using AgentMemory.Packs.CodeMap;
using AgentMemory.Packs.CodeMap.Scip;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

/// <summary>
/// The code map from a SCIP index (coding packs CMP-2b): the records the Roslyn reader writes for C#, for the languages SCIP
/// indexers cover. The index is written here byte for byte, with the symbols scip-python and scip-typescript write.
/// </summary>
public sealed class ScipCodeMapTests
{
    private const string Py = "scip-python python demo 0.1.0 ";
    private const string Store = Py + "`store`/MemoryStore#";
    private const string Add = Py + "`store`/MemoryStore#add().";
    private const string Normalize = Py + "`store`/normalize().";
    private const string Ts = "scip-typescript npm web 1.0.0 src/`cart.ts`/";

    /// <summary>The protobuf wire format, as much of it as an index needs.</summary>
    private sealed class Proto
    {
        private readonly MemoryStream _bytes = new();
        public byte[] ToArray() => _bytes.ToArray();
        private void Raw(ulong value) { do { var b = (byte)(value & 0x7F); value >>= 7; _bytes.WriteByte(value == 0 ? b : (byte)(b | 0x80)); } while (value != 0); }
        private Proto Tag(int field, int type) { Raw((ulong)(field << 3 | type)); return this; }
        public Proto Int(int field, int value) { Tag(field, 0).Raw((ulong)value); return this; }
        public Proto Bytes(int field, byte[] value) { Tag(field, 2).Raw((ulong)value.Length); _bytes.Write(value); return this; }
        public Proto Text(int field, string value) => Bytes(field, Encoding.UTF8.GetBytes(value));
        public Proto Message(int field, Proto message) => Bytes(field, message.ToArray());
        public Proto Packed(int field, params int[] values) { var inner = new Proto(); foreach (var v in values) inner.Raw((ulong)v); return Bytes(field, inner.ToArray()); }
    }

    // An occurrence: the packed (deprecated) range, or the typed one; a definition can carry the range of its body.
    private static Proto At(string symbol, int line, int start, int end, bool definition = false, (int First, int Last)? body = null, bool typed = false)
    {
        var occurrence = new Proto();
        if (typed) occurrence.Message(8, new Proto().Int(1, line).Int(2, start).Int(3, end));
        else occurrence.Packed(1, line, start, end);
        occurrence.Text(2, symbol);
        if (definition) occurrence.Int(3, 1);
        if (body is { } b) occurrence.Message(11, new Proto().Int(1, b.First).Int(2, 0).Int(3, b.Last).Int(4, 1));
        return occurrence;
    }

    private static Proto Document(string path, string language, IEnumerable<Proto> occurrences, params (string Symbol, int Kind, string Display)[] symbols)
    {
        var document = new Proto().Text(1, path);
        foreach (var occurrence in occurrences) document.Message(2, occurrence);
        foreach (var (symbol, kind, display) in symbols) document.Message(3, new Proto().Text(1, symbol).Int(5, kind).Text(6, display));
        return language.Length == 0 ? document : document.Text(4, language);
    }

    private static MemoryStream Index()
    {
        var index = new Proto().Message(1, new Proto().Message(2, new Proto().Text(1, "scip-python").Text(2, "0.6.8")).Text(3, "file:///repo"));
        index.Message(2, Document("store.py", "python",
        [
            At(Store, 0, 6, 17, definition: true, body: (0, 4)),
            At(Add, 1, 8, 11, definition: true),
            At(Py + "`store`/MemoryStore#_hidden().", 3, 8, 15, definition: true),
            At(Normalize, 6, 4, 13, definition: true, typed: true),
            At("local 0", 7, 4, 8),
        ], (Store, 7, "MemoryStore"), (Normalize, 17, "normalize")));
        index.Message(2, Document("tests/test_store.py", "python",
        [
            At(Py + "`tests.test_store`/test_add_then_recall().", 2, 4, 24, definition: true, body: (2, 5)),
            At(Store, 3, 12, 23),
            At(Add, 4, 10, 13, typed: true),
            At(Normalize, 5, 4, 13),
            At(Py + "`tests.test_store`/StoreTests#", 8, 6, 16, definition: true),
            At(Py + "`tests.test_store`/StoreTests#setUp().", 9, 8, 13, definition: true),
            At(Py + "`tests.test_store`/StoreTests#closes_cleanly().", 11, 8, 22, definition: true),
            At(Normalize, 12, 8, 17),
            At("local 3", 12, 20, 21),
            At("scip-python python python-stdlib 3.11 builtins/len().", 13, 8, 11),
        ]));
        index.Message(2, Document("src/cart.ts", "", [At(Ts + "Cart#", 0, 13, 17, definition: true), At(Ts + "Cart#total().", 2, 2, 7, definition: true)]));
        index.Message(2, Document("src/cart.test.ts", "", [At(Ts + "Cart#", 3, 18, 22), At(Ts + "Cart#total().", 4, 14, 19)]));
        return new MemoryStream(index.ToArray());
    }

    private static string Key(string symbol) => symbol.Replace(" 0.1.0 ", " . ", StringComparison.Ordinal).Replace(" 1.0.0 ", " . ", StringComparison.Ordinal);

    [Fact]
    public void A_scip_index_gives_the_records_the_roslyn_reader_writes_for_types_functions_files_projects_and_tests()
    {
        var snapshot = ScipCodeMapReader.Read(Index()).Build("git:one");

        snapshot.Problems().Should().BeEmpty();
        var records = snapshot.Records.ToDictionary(r => (r.Type, r.Key));
        string Property(string type, string key, string name) => records[(type, key)].Properties[name].GetString()!;

        // Types and top-level functions, keyed by SCIP symbol with the version slot emptied so a key survives a release.
        Property(CodeMapPack.SymbolType, Key(Store), "kind").Should().Be("class");
        Property(CodeMapPack.SymbolType, Key(Store), "members").Should().Be("add", "a private member (_hidden) is not named");
        Property(CodeMapPack.SymbolType, Key(Store), "fullName").Should().Be("store/MemoryStore");
        Property(CodeMapPack.SymbolType, Key(Normalize), "kind").Should().Be("function");
        Property(CodeMapPack.SymbolType, Key(Ts + "Cart#"), "kind").Should().Be("type", "scip-typescript does not say what kind of type");
        snapshot.Records.Where(r => r.Type == CodeMapPack.SymbolType).Select(r => r.Key).Should().NotContain(k => k.Contains("StoreTests"),
            "what test files define is not code under test");

        // Files and projects: a project per package; the language from the index, else from the extension.
        Property(CodeMapPack.FileType, "store.py", "language").Should().Be("python");
        Property(CodeMapPack.FileType, "src/cart.ts", "language").Should().Be("typescript");
        records[(CodeMapPack.FileType, "store.py")].Links.Select(l => (l.Relation, l.Target)).Should()
            .Contain([("CODEMAP_IN_PROJECT", "python:demo"), ("CODEMAP_DEFINES", Key(Store)), ("CODEMAP_DEFINES", Key(Normalize))]);
        Property(CodeMapPack.ProjectType, "npm:web", "name").Should().Be("web");

        // A test function links to what its body refers to, most first (the class, twice through its method; then the function).
        var recall = records[(CodeMapPack.TestType, Key(Py + "`tests.test_store`/test_add_then_recall()."))];
        Property(CodeMapPack.TestType, recall.Key, "name").Should().Be("test_store.test_add_then_recall");
        recall.Links.Where(l => l.Relation == "CODEMAP_TESTS").Select(l => l.Target).Should().Equal(Key(Store), Key(Normalize));
        recall.Links.Should().Contain(l => l.Relation == "CODEMAP_DECLARED_IN" && l.Target == "tests/test_store.py");

        // A test class's methods are tests (setUp is not); without a body range, a test runs to the next one.
        var closes = records[(CodeMapPack.TestType, Key(Py + "`tests.test_store`/StoreTests#closes_cleanly()."))];
        Property(CodeMapPack.TestType, closes.Key, "name").Should().Be("StoreTests.closes_cleanly");
        closes.Links.Where(l => l.Relation == "CODEMAP_TESTS").Select(l => l.Target).Should().Equal(Key(Normalize));
        snapshot.Records.Should().NotContain(r => r.Key.Contains("setUp"));

        // A test file whose tests are calls, not symbols (JavaScript's it(...)), is one test.
        records[(CodeMapPack.TestType, "scip-file src/cart.test.ts")].Links.Where(l => l.Relation == "CODEMAP_TESTS").Select(l => l.Target)
            .Should().Equal(Key(Ts + "Cart#"));
    }

    [Fact]
    public void A_new_release_of_the_same_code_gives_the_same_snapshot()
    {
        var first = ScipCodeMapReader.Read(Index()).Build("git:one");
        var bytes = Index().ToArray();
        // Same length, so every protobuf length still holds; Latin-1 both ways keeps every other byte as it is.
        var released = Encoding.Latin1.GetBytes(Encoding.Latin1.GetString(bytes).Replace(" 0.1.0 ", " 0.2.0 ", StringComparison.Ordinal));

        ScipCodeMapReader.Read(new MemoryStream(released)).Build("git:two").Digest.Should().Be(first.Digest);
    }

    [Fact]
    public void A_damaged_index_is_refused_with_a_reason()
    {
        var bytes = Index().ToArray();

        var read = () => ScipCodeMapReader.Read(new MemoryStream(bytes[..^7]));

        read.Should().Throw<InvalidDataException>().WithMessage("A SCIP index *");
    }
}
