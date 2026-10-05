using System.Text.RegularExpressions;
using AgentMemory.RouterArena.Arena;
using AgentMemory.RouterArena.Data;

namespace AgentMemory.RouterArena.Contestants;

/// <summary>
/// 3 · Rules v2, from principles (written before any score, never tuned on one): only small talk reads nothing; a turn
/// recalls what it mentions (semantic, entity-graph, episodic) plus every door rules v1 chose; each other door opens on
/// its own cue words, in English, Spanish and German: a taste or a request (preference), how something is done
/// (procedural), how a task went (reasoning), now (temporal), then (bi-temporal), what is due (prospective), how many,
/// all or the latest (derived), "do you still remember" (forgetting), who she is or what was just said (working).
/// </summary>
public sealed partial class RulesV2(bool rewrite = false) : IContestant
{
    public string Family => "rules";

    public IReadOnlyDictionary<string, object?> Parameters { get; } = new Dictionary<string, object?> { ["rewrite"] = rewrite };

    public Decision Decide(MatrixItem item, TurnContext context)
    {
        if (IsSmallTalk(item.Text)) return Decision.Nothing();
        var doors = new HashSet<Door> { Door.Semantic, Door.EntityGraph, Door.Episodic };
        foreach (var kind in context.Data.Rules.Route(item.Text).Kinds) doors.UnionWith(RulesV1.DoorsOfRoute(kind));
        doors.UnionWith(Cued(item));
        return context.Open(doors, rewrite);
    }

    /// <summary>The doors a turn's own words point at (every door but the three a turn always reads).</summary>
    public static IReadOnlySet<Door> Cued(MatrixItem item)
    {
        var text = item.Text;
        var doors = new HashSet<Door>();
        if (Taste().IsMatch(text)) doors.Add(Door.Preference);
        if (HowTo().IsMatch(text)) doors.Add(Door.Procedural);
        if (HowItWent().IsMatch(text)) doors.Add(Door.Reasoning);
        if (Now().IsMatch(text)) doors.Add(Door.Temporal);
        if (Then().IsMatch(text)) doors.Add(Door.BiTemporal);
        if (Due().IsMatch(text)) doors.Add(Door.Prospective);
        if (Count().IsMatch(text)) doors.Add(Door.Derived);
        if (Faded().IsMatch(text)) doors.Add(Door.Forgetting);
        if (item.Prior.Count > 0 || AboutMe().IsMatch(text)) doors.Add(Door.Working);
        return doors;
    }

    /// <summary>The reading doors a turn's words point at (a gate after the search cannot judge them by similarity).</summary>
    public static IEnumerable<Door> CuedReading(MatrixItem item) => Cued(item).Where(Doors.Reading.Contains);

    private static readonly HashSet<string> SmallTalkWords = new(
        """
        hi hii hiya hello hey morning good night evening thanks thank thx ty cheers ok okay k cool great nice
        perfect lol haha hahaha hehe sure yes yeah yep no nope bye see you later gracias vale genial buenas noches hola danke gute
        super alles klar tschüss adios claro that's that all for now got it love awesome sounds the best merci so much a lot mem again
        then appreciate it you're mucho muchas schön sehr everything help de nada bueno
        """.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), StringComparer.Ordinal);

    [GeneratedRegex(@"[^\W\d_]+(?:'[^\W\d_]+)?")]
    private static partial Regex Word();

    [GeneratedRegex(@"\b(?:like|love|enjoy|prefer|favou?rite|hate|can'?t stand|avoid|recommend|suggest|ideas?|dinner|lunch|eat|food|cook|recipe|drink|coffee|music|film|movie|watch|read|book|gift|present|plan|weekend|trip|restaurant|vegetarian|meat|answer|short|long|bullet|gusta|comida|regalo|essen|geschenk|musik)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Taste();

    [GeneratedRegex(@"\b(?:how (?:do|did|should|can|would) (?:i|we)|how to|steps?|usually|the usual|routine|same (?:way|as last time)|like last time|again|c[oó]mo (?:hago|hice|se hace|reservo)|pasos|normalmente|como siempre|wie (?:mache|buche|habe) ich|schritte|normalerweise|wie immer)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HowTo();

    [GeneratedRegex(@"\b(?:how did [\w'’ ]{1,40}? (?:go|turn out|work out)|how (?:did|was) (?:it|that)|what went wrong|what worked|what (?:did|have) we tr(?:y|ied)|last time we|turn(?:ed)? out|lessons?|c[oó]mo (?:sali[oó]|fue)|qu[eé] funcion[oó]|qu[eé] pas[oó] con|wie (?:lief|war) (?:es|das)|was hat (?:funktioniert|geklappt)|schiefgelaufen)\b", RegexOptions.IgnoreCase)]
    private static partial Regex HowItWent();

    [GeneratedRegex(@"\b(?:now|right now|currently|current|these days|nowadays|at the moment|still|anymore|any more|today|ahora|actualmente|todav[ií]a|sigue|hoy|jetzt|aktuell|derzeit|momentan|immer noch|noch|heute)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Now();

    [GeneratedRegex(@"\b(?:used to|before|previously|back then|back in|in (?:19|20)\d\d|a year ago|years? ago|last year|originally|at first|first (?:said|told)|changed?|former(?:ly)?|old (?:job|address|flat|place)|antes|sol[ií]a|hace (?:un|\w+) a[nñ]os?|el a[nñ]o pasado|cambi[oó]|fr[uü]her|vorher|damals|vor (?:einem|\w+) jahr(?:en)?|letztes jahr|ge[aä]ndert)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Then();

    [GeneratedRegex(@"\b(?:remind(?:er)?|don'?t (?:let me )?forget|forget|due|deadline|coming up|upcoming|this week|next week|soon|to-?do|pending|expir\w*|clos(?:e|es|ing)|recu[eé]rda(?:me)?|recordatorio|pendiente|pr[oó]xima semana|vence|plazo|erinner\w*|f[aä]llig|frist|n[aä]chste woche|bald|vergessen)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Due();

    [GeneratedRegex(@"\b(?:how many|how much|how often|count|number of|total|in total|so far|all (?:the|my)|list|every|latest|most recent|last (?:run|time)|cu[aá]nt[oa]s?|total|tod[oa]s (?:los|las|mis)|[uú]ltim[oa]|lista|wie viele?|wie oft|insgesamt|alle|letzte[nrs]?|liste)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Count();

    [GeneratedRegex(@"\b(?:(?:do|did) you (?:still )?remember|remember when|you forgot|long ago|years ago|did i ever (?:tell|mention)|te acuerdas|recuerdas|hace a[nñ]os|erinnerst du dich|wei[sß]t du noch|vor jahren)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Faded();

    [GeneratedRegex(@"\b(?:about me|who am i|know about me|lately|these days|what (?:did )?(?:i|you) just say|you just said|as i said|earlier|above|sobre m[ií]|qui[eé]n soy|[uú]ltimamente|acabas de decir|[uü]ber mich|wer bin ich|in letzter zeit|gerade gesagt)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AboutMe();

    /// <summary>Every word is a greeting, thanks or acknowledgement word (or there are no words at all).</summary>
    public static bool IsSmallTalk(string text) =>
        Word().Matches(text.ToLowerInvariant()).All(m => SmallTalkWords.Contains(m.Value));
}
