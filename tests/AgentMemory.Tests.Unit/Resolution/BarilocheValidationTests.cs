using AgentMemory.Scenarios;
using FluentAssertions;
namespace AgentMemory.Tests.Unit.Resolution;
public sealed class BarilocheValidationTests
{
    private static Scenario Scenario() => new("bruna-go-live","original","source","bruna","Bruna","brief",
        new DateOnly(2026,12,4), Enumerable.Range(0,8).Select(i=>new Session(new DateOnly(2026,9,1).AddDays(i),"session",
            Enumerable.Repeat("unchanged source statement",i==7?7:6).ToArray())).ToArray(),
        new[]{"similar","time","conflict"}.Select(id=>new Question(id,"kind","unchanged question",
            [new Need("original",[["original"]])],"unchanged gold answer")).ToArray(),[]);
    [Fact]
    public void Stronger_checks_do_not_change_history_questions_answers_or_original_fingerprint()
    {
        var original=Scenario() with { Sha256="original-file-hash" };var result=BarilocheValidation.Apply(original);
        result.Sessions.Should().BeSameAs(original.Sessions);result.Sha256.Should().Be(original.Sha256);
        result.Questions.Select(q=>q.Text).Should().Equal(original.Questions.Select(q=>q.Text));
        result.Questions.Select(q=>q.RightAnswer).Should().Equal(original.Questions.Select(q=>q.RightAnswer));
        result.Questions.Take(2).Should().Equal(original.Questions.Take(2));
        result.Questions[2].Needs.Select(n=>n.Fact).Should().Equal("original","Go-live location","Presence required");
        BarilocheValidation.Apply(result).Questions[2].Needs.Should().HaveCount(3);
    }
    [Fact]
    public void Full_profile_rejects_a_short_case_or_busy_variant()
    {
        var wrongName=()=>BarilocheValidation.Apply(Scenario() with { Name="bruna-go-live-busy" });
        wrongName.Should().Throw<ArgumentException>();
        var tooShort=()=>BarilocheValidation.Apply(Scenario() with { Sessions=[] });
        tooShort.Should().Throw<ArgumentException>();
    }
    [Fact]
    public void Full_conflict_checks_reject_date_only_prompt_and_event_name_keyword_contamination()
    {
        var q=BarilocheValidation.Apply(Scenario()).Questions[2];
        var result=Checks.Needs(q,"original; Acme go-live December11; Bariloche December9-13");
        result.Where(n=>!n.Found).Select(n=>n.Fact).Should().Contain("Go-live location").And.Contain("Presence required");
        Checks.EventDetailFailures(["dated event"],[new("Cordoba go-live","status","discussed")],BarilocheValidation.EventNeeds)
            .Should().HaveCount(2);
    }
}
