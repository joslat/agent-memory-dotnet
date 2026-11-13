using AgentMemory.Scenarios;
using FluentAssertions;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityScenarioAcceptanceTests
{
    [Fact]
    public void Correct_prompt_does_not_hide_the_two_missing_date_fields_observed_in_the_real_run()
    {
        var result=Checks.ValidationFailures(
            [new("Training December 3",["dated training"],["dated training"]),
             new("Code freeze December 4",[],["code freeze is scheduled for 2026-12-04"]),
             new("Wedding December 12",[],["wedding is scheduled for 2026-12-12"])],
            [new("Wedding December 12","wedding is scheduled for 2026-12-12")]);
        result.Should().HaveCount(2).And.Contain("store: Code freeze December 4").And.Contain("store: Wedding December 12");
    }

    [Fact]
    public void Correct_store_does_not_hide_missing_place_evidence_in_the_prompt()
    {
        Checks.ValidationFailures([new("Go-live",["stored"],["stored"])],[new("Córdoba location",null)])
            .Should().ContainSingle().Which.Should().Be("prompt: Córdoba location");
    }

    [Fact]
    public void Both_correct_layers_have_no_deterministic_failure_without_grading_the_answer_model()
    {
        Checks.ValidationFailures([new("Wedding",["stored"],["stored"])],[new("Wedding date","December 12")]).Should().BeEmpty();
    }
    private static readonly Need Place = new("Go-live location", [["go-live"], ["Córdoba", "Cordoba"]]);
    private static readonly Need Presence = new("Presence required", [["go-live"], ["in person", "on-site"]]);

    [Fact]
    public void All_facts_in_the_normal_prompt_do_not_prove_the_event_hop()
    {
        Checks.ValidationFailures([], [new("Go-live location", "present"), new("Presence required", "present")]).Should().BeEmpty();
        Checks.EventDetailFailures(["Acme go-live on December 11"], [], [Place, Presence]).Should().HaveCount(2);
    }

    [Fact]
    public void Another_event_in_the_right_city_does_not_satisfy_event_detail_support()
    {
        Checks.EventDetailFailures(["Acme go-live on December 11"],
            [new("Other conference","is in","Córdoba"), new("Acme go-live","requires","in person")], [Place, Presence])
            .Should().Equal("event-details: Go-live location");
    }

    [Fact]
    public void Dated_seeds_and_both_undated_event_details_satisfy_the_native_probe()
    {
        Checks.EventDetailFailures(["Acme go-live on December 11"],
            [new("Acme go-live","is in","Córdoba"), new("Acme go-live","requires","in person")], [Place, Presence]).Should().BeEmpty();
        Checks.EventDetailFailures([], [new("Acme go-live","is in","Córdoba"), new("Acme go-live","requires","in person")], [Place, Presence])
            .Should().Contain("event-details: no dated seed facts");
    }

    [Fact]
    public void Remote_in_the_event_name_is_not_permission_evidence()
    {
        var remote=new Need("Remote go-live",[["go-live"],["remote","remotely"]]);
        Checks.EventDetailFailures(["dated event"], [new("user","leads","Acme remote go-live")], [remote])
            .Should().Equal("event-details: Remote go-live");
        Checks.EventDetailFailures(["dated event"], [new("Acme remote go-live","participation","user may run it remotely")], [remote])
            .Should().BeEmpty();
    }

    [Fact]
    public void Detail_keywords_in_a_name_do_not_substitute_for_a_location_or_participation_claim()
    {
        Checks.EventDetailFailures(["dated event"],
            [new("Cordoba go-live","status","discussed"), new("in person go-live","status","discussed")],
            [Place,Presence]).Should().HaveCount(2);
    }

}
