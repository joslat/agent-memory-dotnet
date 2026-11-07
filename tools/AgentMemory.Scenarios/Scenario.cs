using System.Security.Cryptography;
using System.Text.Json;

namespace AgentMemory.Scenarios;

/// <summary>
/// A scripted history (sessions of things one person said, over months), the questions asked afterwards, and what the
/// store and the prompt must hold. The format is Pablo Piovano's, from three-memories-one-question (MIT), with
/// <see cref="Store"/> added: the facts the store must hold once the history is written.
/// </summary>
internal sealed record Scenario(
    string Name,
    string Title,
    string? Source,
    string Owner,
    string? OwnerName,
    string AssistantBrief,
    DateOnly AskedOn,
    IReadOnlyList<Session> Sessions,
    IReadOnlyList<Question> Questions,
    IReadOnlyList<StoreCheck>? Store)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>The file's SHA-256, so two runs are only compared when they ran the same scenario.</summary>
    public string Sha256 { get; init; } = "";

    public static Scenario Load(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var scenario = JsonSerializer.Deserialize<Scenario>(bytes, Json)
            ?? throw new InvalidDataException($"'{path}' is not a scenario.");
        return scenario with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)) };
    }

    /// <summary>The moment the questions are asked: the morning of <see cref="AskedOn"/>, in UTC.</summary>
    public DateTimeOffset AskedAt => new(AskedOn.ToDateTime(new TimeOnly(9, 0)), TimeSpan.Zero);

    /// <summary>Every statement in the order it was said, each stamped with its own moment (two minutes apart).</summary>
    public IEnumerable<Turn> Turns()
    {
        for (var s = 0; s < Sessions.Count; s++)
        {
            var session = Sessions[s];
            var start = new DateTimeOffset(session.Date.ToDateTime(new TimeOnly(10, 0)), TimeSpan.Zero);
            for (var m = 0; m < session.Messages.Count; m++)
            {
                yield return new Turn($"s{s + 1}", start.AddMinutes(m * 2), session.Messages[m]);
            }
        }
    }
}

internal sealed record Session(DateOnly Date, string Topic, IReadOnlyList<string> Messages);

/// <summary>
/// A question, the facts the prompt must contain for it to be answerable, and what a right answer says. The right
/// answer is printed beside the model's for a person to read; nothing here grades it.
/// </summary>
internal sealed record Question(string Id, string Kind, string Text, IReadOnlyList<Need> Needs, string RightAnswer);

/// <summary>
/// One fact the prompt must contain. It is there when one item of the prompt (a line, or one entry of a list the
/// memory joined with "; ") has, for every group in <see cref="Mentions"/>, at least one of its spellings:
/// [["go-live"], ["December 11", "2026-12-11"]] reads "the go-live and its date, in the same item". Two words anywhere
/// in the prompt are not a fact: "go-live starts at 7 am" and an unrelated "Friday, December 11 (EVENT)" never were.
/// </summary>
internal sealed record Need(string Fact, IReadOnlyList<IReadOnlyList<string>> Mentions);

/// <summary>
/// One fact the store must hold once the history is written: a live fact whose subject, predicate and object together
/// have every group of <see cref="Mentions"/>, dated as given. <see cref="Date"/> is the day it happens or starts
/// (occurred_on or valid_from); <see cref="From"/> and <see cref="Until"/> are a stay's first and last day.
/// </summary>
internal sealed record StoreCheck(
    string Fact, IReadOnlyList<IReadOnlyList<string>> Mentions, DateOnly? Date = null, DateOnly? From = null, DateOnly? Until = null);

/// <summary>One thing the person said, in one session, at one moment.</summary>
internal sealed record Turn(string Session, DateTimeOffset At, string Text);
