using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace AgentMemory.Scenarios.PublicGold;

/// <summary>
/// ECB+ (Cybulska and Vossen, LREC 2014; annotations CC BY 3.0) as an identity scenario and its answer key. Each topic
/// holds two subtopics: two similar but different real events (two rehab check-ins, two earthquakes). Every article
/// becomes one session of things the user shares; its annotated sentences are said verbatim, in order; the gold groups
/// are the corpus's cross-document coreference instances, so the labels stay the annotators'. No model rewrites text.
/// </summary>
/// <remarks>
/// The slice file holds only identifiers and hashes of the selected sentences, never article text: the text is read
/// from the downloaded corpus at run time and must match the hashes the slice was frozen with.
/// </remarks>
internal static partial class EcbPlusAdapter
{
    public const string SliceVersion = "ecbplus-slice-v1";
    public const string Source = "ECB+ (Cybulska and Vossen, LREC 2014), https://github.com/cltl/ecbPlus";
    public const string Licence = "Annotations CC BY 3.0 Unported, (c) Agata Cybulska, Piek Vossen and VU University Amsterdam; article texts are third-party news and are not redistributed here.";
    private const string SentencesFile = "ECBplus_coreference_sentences.csv";

    /// <summary>
    /// Conversion v2: a personal memory rightly stores nothing of general news (verbatim sentences stored 0 facts in
    /// igb-ecbplus-one-01), so each sentence is said as part of the user's work. One fixed frame; the source words follow
    /// unchanged and the labels stay the annotators'.
    /// </summary>
    public const string Framing = "I'm covering this story at work: ";
    public const string Conversion = "ecbplus-conversion-v2 (verbatim sentence after one fixed work frame)";
    private static readonly DateOnly FirstDay = new(2026, 1, 5);

    // ── Selection (frozen into the slice file before any run) ───────────────

    public static EcbPlusSlice Select(string datasetDirectory, int topics = 4, int documentsPerEvent = 4)
    {
        var corpus = CorpusFolder(datasetDirectory);
        var annotated = AnnotatedSentences(datasetDirectory);
        var selected = new List<EcbTopic>();
        foreach (var folder in Directory.GetDirectories(corpus)
                     .Select(path => (Path: path, Number: int.TryParse(Path.GetFileName(path), CultureInfo.InvariantCulture, out var n) ? n : -1))
                     .Where(f => f.Number >= 0).OrderBy(f => f.Number))
        {
            if (selected.Count == topics) break;
            var subtopics = new List<EcbSubtopic>();
            foreach (var subtopic in new[] { "ecb", "ecbplus" })
            {
                var documents = Documents(folder.Path, folder.Number, subtopic)
                    .Select(file => (File: file, Parsed: Parse(Path.Combine(folder.Path, file)), Sentences: Annotated(annotated, file, subtopic)))
                    .ToList();
                var seminal = documents
                    .SelectMany(d => d.Parsed.Mentions.Where(m => KindOf(m.InstanceTag) == "EVENT" && d.Sentences.Contains(SentenceOf(d.Parsed, m)))
                        .Select(m => (m.Instance, m.Descriptor, d.File)))
                    .GroupBy(m => m.Instance, StringComparer.Ordinal)
                    .Select(g => (Instance: g.Key, g.First().Descriptor, Documents: g.Select(m => m.File).Distinct(StringComparer.Ordinal).Count()))
                    .OrderByDescending(g => g.Documents).ThenBy(g => g.Instance, StringComparer.Ordinal)
                    .FirstOrDefault();
                if (seminal.Instance is null) break;
                var chosen = documents
                    .Where(d => d.Parsed.Mentions.Any(m => m.Instance == seminal.Instance && d.Sentences.Contains(SentenceOf(d.Parsed, m))))
                    .Take(documentsPerEvent)
                    .Select(d => new EcbDocumentRef(d.File, d.Sentences.Order()
                        .Where(n => d.Parsed.Tokens.Any(t => t.Sentence == n))
                        .Select(n => new EcbSentenceRef(n, Hash(d.Parsed, n))).ToList()))
                    .ToList();
                subtopics.Add(new EcbSubtopic(subtopic, seminal.Instance, seminal.Descriptor, chosen));
            }

            if (subtopics.Count == 2) selected.Add(new EcbTopic(folder.Number, subtopics));
        }

        return new EcbPlusSlice(SliceVersion, Source, Licence, Rule(topics, documentsPerEvent),
            Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Path.Combine(datasetDirectory, SentencesFile)))), selected);
    }

    private static string Rule(int topics, int documents) =>
        $"The first {topics} ECB+ topics in numeric order, both subtopics of each (ecb and ecbplus: two similar but different " +
        "real events). Seminal event of a subtopic: the cross-document action instance (reporting acts excluded) mentioned " +
        "in the most documents within the coreference-annotated sentences (ECBplus_coreference_sentences.csv); ties by " +
        $"instance id. Documents: the first {documents} by document number that mention the seminal event in an annotated " +
        "sentence. Sentences: every annotated sentence of those documents; sentence 0 of an ecbplus file (its source URL) " +
        "excluded. Sessions: per topic, one article at a time, the two subtopics alternating.";

    // ── Building the scenario and its gold from a frozen slice ──────────────

    public static (Scenario Scenario, IdentityGold Gold) Build(EcbPlusSlice slice, string datasetDirectory, int? sessions = null)
    {
        var corpus = CorpusFolder(datasetDirectory);
        var order = new List<(int Topic, EcbSubtopic Subtopic, EcbDocumentRef Document)>();
        foreach (var topic in slice.Topics)
            for (var i = 0; i < topic.Subtopics.Max(s => s.Documents.Count); i++)
                foreach (var subtopic in topic.Subtopics.Where(s => i < s.Documents.Count))
                    order.Add((topic.Topic, subtopic, subtopic.Documents[i]));
        if (sessions is { } limit) order = order.Take(limit).ToList();

        var built = new List<Session>();
        var statementGroups = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var partitions = new Dictionary<string, string>(StringComparer.Ordinal);
        var groups = new Dictionary<string, (string Kind, string Descriptor, List<IReadOnlyList<string>> Mentions)>(StringComparer.Ordinal);
        var statement = 0;
        foreach (var (topic, subtopic, document) in order)
        {
            var parsed = Parse(Path.Combine(corpus, topic.ToString(CultureInfo.InvariantCulture), document.File));
            var messages = new List<string>();
            foreach (var sentence in document.Sentences)
            {
                if (Hash(parsed, sentence.Sentence) != sentence.Sha256)
                    throw new InvalidDataException($"ECB+ {document.File} sentence {sentence.Sentence} does not match {slice.Version}: the corpus differs from the one the slice was frozen on.");
                messages.Add(Framing + Detokenize(parsed.Tokens.Where(t => t.Sentence == sentence.Sentence).Select(t => t.Text)));
                statement++;
                var key = statement.ToString(CultureInfo.InvariantCulture);
                partitions[key] = $"{topic}/{subtopic.Subtopic}";
                var mentioned = new List<string>();
                foreach (var mention in parsed.Mentions.Where(m => SentenceOf(parsed, m) == sentence.Sentence))
                {
                    if (KindOf(mention.InstanceTag) is not { } kind) continue;
                    if (!groups.TryGetValue(mention.Instance, out var group))
                        groups[mention.Instance] = group = (kind, mention.Descriptor, []);
                    group.Mentions.Add(mention.Tokens.Select(id => parsed.Tokens.First(t => t.Id == id).Text).ToList());
                    if (!mentioned.Contains(mention.Instance, StringComparer.Ordinal)) mentioned.Add(mention.Instance);
                }

                statementGroups[key] = mentioned;
            }

            built.Add(new Session(FirstDay.AddDays(built.Count), "news the user shared", messages));
        }

        var scenario = NewScenario(slice.Version, "ECB+ events (independent identity gold), " + Conversion, Source + "; " + Licence, built);
        var keywords = groups.ToDictionary(g => g.Key, g => Keywords(g.Value.Kind, g.Value.Mentions, g.Value.Descriptor), StringComparer.Ordinal);
        // An event's annotator descriptor often names its participants ("t1b_tara_reid_checks_in"): those words name the
        // people and places, not the event, so they are not event keywords.
        var participants = groups.Where(g => g.Value.Kind != "EVENT").SelectMany(g => keywords[g.Key]).ToHashSet(StringComparer.Ordinal);
        var gold = new IdentityGold("ecbplus-gold-v1-derived-from-" + slice.Version, scenario.Sha256,
            groups.ToDictionary(g => g.Key, g => new GoldGroup(g.Value.Kind,
                g.Value.Kind == "EVENT" ? keywords[g.Key].Where(k => !participants.Contains(k)).ToList() : keywords[g.Key]), StringComparer.Ordinal),
            statementGroups, IdentityGold.WordPrefix, partitions);
        return (scenario, gold);
    }

    internal static Scenario NewScenario(string name, string title, string source, IReadOnlyList<Session> sessions)
    {
        var scenario = new Scenario(name, title, source, "reader", "Reader",
            "You are a helpful assistant. You remember what the user shared with you.",
            sessions.Count == 0 ? FirstDay : sessions[^1].Date.AddDays(1), sessions, [], []);
        return scenario with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(scenario, IdentityGold.Json))) };
    }

    // ── Corpus reading ─────────────────────────────────────────────────────

    internal sealed record Token(int Id, int Sentence, string Text);
    internal sealed record Mention(IReadOnlyList<int> Tokens, string Instance, string InstanceTag, string Descriptor);
    internal sealed record Document(IReadOnlyList<Token> Tokens, IReadOnlyList<Mention> Mentions);

    /// <summary>Tokens, and every mention that takes part in a cross-document coreference chain, with its instance.</summary>
    internal static Document Parse(string path)
    {
        using var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        var root = XDocument.Load(reader).Root ?? throw new InvalidDataException($"'{path}' is not an ECB+ document.");
        var tokens = root.Elements("token").Select(t => new Token(Int(t, "t_id"), Int(t, "sentence"), t.Value)).ToList();
        var markables = root.Element("Markables")?.Elements().ToList() ?? [];
        var anchors = markables.Where(m => m.Elements("token_anchor").Any())
            .ToDictionary(m => (string)m.Attribute("m_id")!, m => m.Elements("token_anchor").Select(a => Int(a, "t_id")).ToList(), StringComparer.Ordinal);
        var instances = markables.Where(m => m.Attribute("instance_id") is not null)
            .ToDictionary(m => (string)m.Attribute("m_id")!, m => (Id: (string)m.Attribute("instance_id")!, Tag: m.Name.LocalName,
                Descriptor: (string?)m.Attribute("TAG_DESCRIPTOR") ?? ""), StringComparer.Ordinal);
        var mentions = new List<Mention>();
        foreach (var relation in root.Element("Relations")?.Elements("CROSS_DOC_COREF") ?? [])
        {
            var target = (string?)relation.Element("target")?.Attribute("m_id");
            if (target is null || !instances.TryGetValue(target, out var instance)) continue;
            foreach (var source in relation.Elements("source").Select(s => (string?)s.Attribute("m_id")))
                if (source is not null && anchors.TryGetValue(source, out var anchored))
                    mentions.Add(new Mention(anchored, instance.Id, instance.Tag, instance.Descriptor));
        }

        return new Document(tokens, mentions);
    }

    private static int Int(XElement element, string attribute) =>
        int.Parse((string?)element.Attribute(attribute) ?? throw new InvalidDataException($"ECB+ element without {attribute}."), CultureInfo.InvariantCulture);

    private static int SentenceOf(Document document, Mention mention) =>
        document.Tokens.First(t => t.Id == mention.Tokens[0]).Sentence;

    /// <summary>EVENT, PERSON, ORGANIZATION, LOCATION, or null for what is not scored (reporting acts, times, objects).</summary>
    internal static string? KindOf(string tag) => tag switch
    {
        _ when tag.Contains("REPORTING", StringComparison.Ordinal) => null,
        _ when tag.StartsWith("ACTION_", StringComparison.Ordinal) || tag.StartsWith("NEG_ACTION_", StringComparison.Ordinal) => "EVENT",
        "HUMAN_PART_PER" => "PERSON",
        "HUMAN_PART_ORG" => "ORGANIZATION",
        "HUMAN_PART_GPE" => "LOCATION",
        _ when tag.StartsWith("LOC_", StringComparison.Ordinal) => "LOCATION",
        _ => null,
    };

    private static string CorpusFolder(string datasetDirectory)
    {
        foreach (var candidate in new[] { Path.Combine(datasetDirectory, "ECB+"), Path.Combine(datasetDirectory, "extracted", "ECB+") })
            if (Directory.Exists(candidate)) return candidate;
        throw new DirectoryNotFoundException($"No ECB+ corpus under '{datasetDirectory}': unzip ECB+.zip there (folder ECB+ or extracted/ECB+).");
    }

    private static Dictionary<string, SortedSet<int>> AnnotatedSentences(string datasetDirectory)
    {
        var sentences = new Dictionary<string, SortedSet<int>>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(Path.Combine(datasetDirectory, SentencesFile)).Skip(1))
        {
            var cells = line.Split(',');
            if (cells.Length < 3) continue;
            var file = $"{cells[0].Trim()}_{cells[1].Trim()}.xml";
            (sentences.TryGetValue(file, out var set) ? set : sentences[file] = []).Add(int.Parse(cells[2], CultureInfo.InvariantCulture));
        }

        return sentences;
    }

    private static SortedSet<int> Annotated(Dictionary<string, SortedSet<int>> annotated, string file, string subtopic)
    {
        var set = annotated.TryGetValue(file, out var listed) ? new SortedSet<int>(listed) : [];
        if (subtopic == "ecbplus") set.Remove(0);
        return set;
    }

    private static IEnumerable<string> Documents(string folder, int topic, string subtopic) =>
        Directory.GetFiles(folder, "*.xml")
            .Select(Path.GetFileName).OfType<string>()
            .Select(file => (File: file, Match: DocumentName().Match(file)))
            .Where(f => f.Match.Success && f.Match.Groups[1].Value == topic.ToString(CultureInfo.InvariantCulture) && f.Match.Groups[3].Value == subtopic)
            .OrderBy(f => int.Parse(f.Match.Groups[2].Value, CultureInfo.InvariantCulture))
            .Select(f => f.File);

    [GeneratedRegex(@"^(\d+)_(\d+)(ecb|ecbplus)\.xml$")]
    private static partial Regex DocumentName();

    /// <summary>The sentence's identity in the corpus: its raw tokens joined by single spaces, hashed.</summary>
    private static string Hash(Document document, int sentence) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(' ', document.Tokens.Where(t => t.Sentence == sentence).Select(t => t.Text)))));

    /// <summary>Tokens back into a sentence a person would type: no space before closing punctuation or after an opening one.</summary>
    internal static string Detokenize(IEnumerable<string> tokens)
    {
        var text = new StringBuilder();
        var glue = false;
        foreach (var raw in tokens)
        {
            var token = raw switch { "``" or "''" => "\"", "-LRB-" => "(", "-RRB-" => ")", _ => raw };
            var opening = raw is "``" or "(" or "[" or "{" or "$" or "-LRB-";
            var closing = raw is "''" or "," or "." or ";" or ":" or "!" or "?" or ")" or "]" or "}" or "%" or "-RRB-"
                || raw.Equals("n't", StringComparison.OrdinalIgnoreCase)
                || raw.Length is > 1 and <= 3 && raw[0] == '\'' && char.IsLetter(raw[1]);
            if (text.Length > 0 && !glue && !closing) text.Append(' ');
            text.Append(token);
            glue = opening;
        }

        return text.ToString();
    }

    // ── Keywords ───────────────────────────────────────────────────────────

    private static readonly HashSet<string> Stop = new(StringComparer.Ordinal)
    {
        "the", "a", "an", "of", "in", "on", "at", "to", "into", "for", "with", "by", "from", "and", "or", "as", "is", "was",
        "were", "be", "been", "has", "had", "have", "its", "it", "he", "she", "they", "him", "her", "his", "hers", "their",
        "them", "who", "whom", "which", "that", "this", "these", "those", "i", "we", "you", "our", "your", "my", "me", "us",
        "there", "here", "after", "before", "about", "over", "out", "up", "not", "but", "also", "one", "two", "new",
    };

    /// <summary>
    /// Words that name one gold group: each multi-word mention as a phrase, each content word of its mentions and of the
    /// annotators' descriptor, and for events also each word's stem ("checked" gives "check"), matched as word prefixes.
    /// </summary>
    internal static IReadOnlyList<string> Keywords(string kind, IEnumerable<IReadOnlyList<string>> mentions, string descriptor)
    {
        var keywords = new SortedSet<string>(StringComparer.Ordinal);
        void Add(string word)
        {
            if (word.Length < 3 || Stop.Contains(word) || word.All(char.IsDigit)) return;
            keywords.Add(word);
            if (kind == "EVENT") keywords.Add(Stem(word));
        }

        foreach (var mention in mentions)
        {
            var words = mention.Select(Clean).Where(w => w.Length > 0).ToList();
            if (words.Count > 1 && words.Any(w => !Stop.Contains(w))) keywords.Add(string.Join(' ', words));
            words.ForEach(Add);
        }

        foreach (var word in descriptor.Split('_').Skip(1)) Add(Clean(word));
        return keywords.ToList();
    }

    private static string Clean(string word) => word.ToLowerInvariant().Trim().Trim(Array.FindAll(word.ToCharArray(), c => !char.IsLetterOrDigit(c)));

    private static string Stem(string word)
    {
        foreach (var suffix in new[] { "ings", "ing", "ed", "es", "s", "ment", "ion" })
            if (word.EndsWith(suffix, StringComparison.Ordinal) && word.Length - suffix.Length >= 4) return word[..^suffix.Length];
        return word;
    }

    // ── Slice file ─────────────────────────────────────────────────────────

    public static string SliceJson(EcbPlusSlice slice) => JsonSerializer.Serialize(slice, IdentityGold.Json);

    public static EcbPlusSlice LoadSlice(string path) =>
        JsonSerializer.Deserialize<EcbPlusSlice>(File.ReadAllBytes(path), IdentityGold.Json)
        ?? throw new InvalidDataException($"'{path}' is not an ECB+ slice.");
}

/// <summary>A frozen ECB+ selection: identifiers and sentence hashes only, no corpus text.</summary>
internal sealed record EcbPlusSlice(string Version, string Source, string Licence, string Rule, string SentencesCsvSha256, IReadOnlyList<EcbTopic> Topics);

internal sealed record EcbTopic(int Topic, IReadOnlyList<EcbSubtopic> Subtopics);

/// <summary>One subtopic: its seminal event's instance id and annotator descriptor, and the documents chosen for it.</summary>
internal sealed record EcbSubtopic(string Subtopic, string Seminal, string Descriptor, IReadOnlyList<EcbDocumentRef> Documents);

internal sealed record EcbDocumentRef(string File, IReadOnlyList<EcbSentenceRef> Sentences);

/// <summary>A sentence number and the SHA-256 of its raw tokens joined by single spaces.</summary>
internal sealed record EcbSentenceRef(int Sentence, string Sha256);
