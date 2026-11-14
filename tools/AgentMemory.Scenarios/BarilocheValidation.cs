namespace AgentMemory.Scenarios;

/// <summary>Additional frozen assertions for the original full49-statement scenario, never model input.</summary>
internal static class BarilocheValidation
{
    internal const string Version="bariloche-identity-v1";
    internal static IReadOnlyList<Need> EventNeeds { get; } =
    [
        new("Go-live location", [["go-live","go live","cutover"],["C\u00f3rdoba","Cordoba"]]),
        new("Presence required", [["go-live","go live","cutover"],["in person","in-person","on-site","onsite","physically present"]])
    ];

    internal static Scenario Apply(Scenario scenario)
    {
        // The original 49 statements, or its busy 130-statement variant (same questions, many distractors): the
        // answer-level discriminator between vector luck and the decision-bound event read.
        var original=scenario.Name=="bruna-go-live" && scenario.Sessions.Count==8 && scenario.Turns().Count()==49;
        var busy=scenario.Name=="bruna-go-live-busy" && scenario.Sessions.Count==17 && scenario.Turns().Count()==130;
        // ILC-33: the busy history with two people named Alex Kim (134 statements) and two questions about people.
        var people=scenario.Name=="bruna-go-live-people" && scenario.Sessions.Count==17 && scenario.Turns().Count()==134;
        string[] questions=people?["similar","time","conflict","person","homonym"]:["similar","time","conflict"];
        if(!(original || busy || people) ||
            scenario.Questions.Count!=questions.Length || !scenario.Questions.Select(q=>q.Id).ToHashSet(StringComparer.Ordinal)
                .SetEquals(questions))
            throw new ArgumentException("The full profile accepts the original 49-statement Bariloche scenario, its 130-statement busy variant or the 134-statement people variant only.",nameof(scenario));
        return scenario with { Questions=scenario.Questions.Select(q=>q.Id=="conflict"
            ?q with { Needs=q.Needs.Concat(EventNeeds).DistinctBy(n=>n.Fact).ToArray() }:q).ToArray() };
    }
}
