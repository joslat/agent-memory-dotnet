using System.Net;
using System.Text;
using System.Text.Json;
using AgentMemory.Inference;
using AgentMemory.Scenarios;
using FluentAssertions;
using Microsoft.Extensions.AI;

namespace AgentMemory.Tests.Unit.Resolution;

public sealed class IdentityInferenceBudgetTests : IDisposable
{
    private readonly string _folder=Path.Combine(Path.GetTempPath(),"identity-budget-"+Guid.NewGuid().ToString("N"));
    private int _hits;
    private readonly List<string> _requests=[];
    private string? _requestRecordedBeforeDispatch;
    private static InferenceProviderSettings Settings=>new()
    { Provider=InferenceProvider.Bitdeer,Endpoint=IdentityValidationClients.Endpoint,ApiKey="fake-not-a-live-key",
        Model=IdentityValidationClients.ChatModel,EmbeddingModel=IdentityValidationClients.EmbeddingModel,EmbeddingDimensions=1024 };
    private string Campaign=>Path.Combine(_folder,"campaign.jsonl");
    private IdentityValidationClients Client(bool fail=false,bool inspectBeforeDispatch=false,bool full=false)
    {
        Directory.CreateDirectory(_folder);
        var callsPath=Path.Combine(_folder,Guid.NewGuid()+".jsonl");
        return new(Settings,Campaign,callsPath,role=>new FakeHandler(async request=>
        {
            _hits++; var sent=await request.Content!.ReadAsStringAsync(); _requests.Add(sent);
            if(inspectBeforeDispatch)
                foreach(var line in ReadShared(callsPath))
                {
                    using var entry=JsonDocument.Parse(line);
                    if(entry.RootElement.TryGetProperty("request",out var snapshot)) _requestRecordedBeforeDispatch=snapshot.GetString();
                }
            var embedding=role=="embedding";
            var body=embedding?"{\"object\":\"list\",\"model\":\"BAAI/bge-m3\",\"data\":[{\"object\":\"embedding\",\"index\":0,\"embedding\":[0.1,0.2]}],\"usage\":{\"prompt_tokens\":2,\"total_tokens\":2}}":
                "{\"id\":\"smoke-test\",\"object\":\"chat.completion\",\"created\":0,\"model\":\"zai-org/GLM-5.3-Flash\",\"choices\":[{\"index\":0,\"message\":{\"role\":\"assistant\",\"content\":\"{}\"},\"finish_reason\":\"stop\"}],\"usage\":{\"prompt_tokens\":2,\"completion_tokens\":1,\"total_tokens\":3}}";
            return new HttpResponseMessage(fail?HttpStatusCode.InternalServerError:HttpStatusCode.OK)
                { Content=new StringContent(body,Encoding.UTF8,"application/json") };
        }),writerMode:full,fullScenario:full);
    }
    private string[] ReadCampaign()=>ReadShared(Campaign);
    private static string[] ReadShared(string path)
    {
        using var reader=new StreamReader(new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.ReadWrite));
        return reader.ReadToEnd().Split(Environment.NewLine,StringSplitOptions.RemoveEmptyEntries);
    }
    private static Task<ChatResponse> Ask(IChatClient client,ChatOptions? options=null,string text="The wedding is December 12, 2026.")=>
        client.GetResponseAsync([new ChatMessage(ChatRole.User,text)],options);

    [Fact]
    public async Task Missing_output_cap_is_bounded_before_transport_and_a_positive_reservation_is_durable()
    {
        using var clients=Client();
        await Ask(clients.Writer);
        _hits.Should().Be(1);
        using var request=JsonDocument.Parse(_requests.Single());
        request.RootElement.GetProperty("max_tokens").GetInt32().Should().Be(4096);
        using var reservation=JsonDocument.Parse(ReadCampaign().Single());
        reservation.RootElement.GetProperty("usd").GetDecimal().Should().BeGreaterThan(0).And.BeLessThan(1);
    }

    [Theory]
    [InlineData("writer")] [InlineData("answer")] [InlineData("adjudicator")]
    public async Task Reasoning_model_roles_explicitly_request_low_effort_with_the_same_output_cap(string role)
    {
        using var clients=Client(inspectBeforeDispatch:true);
        var chat=role=="writer"?clients.Writer:role=="answer"?clients.Answer:clients.Adjudicator;
        await Ask(chat,new ChatOptions { MaxOutputTokens=1024 });
        _hits.Should().Be(1);
        using var request=JsonDocument.Parse(_requests.Single());
        request.RootElement.GetProperty("reasoning_effort").GetString().Should().Be("low");
        var root=request.RootElement;
        (root.TryGetProperty("max_completion_tokens",out var cap)?cap:root.GetProperty("max_tokens"))
            .GetInt32().Should().Be(1024);
        _requestRecordedBeforeDispatch.Should().Be(_requests.Single());
    }

    [Theory]
    [InlineData(4097,false)] [InlineData(1024,true)]
    public async Task Excess_output_or_input_is_refused_without_sending_or_reserving(int output,bool oversizedInput)
    {
        using var clients=Client();
        var act=()=>Ask(clients.Writer,new ChatOptions { MaxOutputTokens=output },oversizedInput?new string('x',40000):"short");
        await act.Should().ThrowAsync<Exception>();
        _hits.Should().Be(0); ReadCampaign().Should().BeEmpty();
    }

    [Fact]
    public async Task Writer_answer_and_adjudicator_share_one_persistent_chat_attempt_cap_across_restart()
    {
        using(var clients=Client())
            for(var i=0;i<IdentityValidationClients.FullScenarioChatAttempts;i++) await Ask(i%3==0?clients.Writer:i%3==1?clients.Answer:clients.Adjudicator);
        using var restarted=Client();
        var act=()=>Ask(restarted.Writer); await act.Should().ThrowAsync<Exception>();
        _hits.Should().Be(IdentityValidationClients.FullScenarioChatAttempts); ReadCampaign().Should().HaveCount(IdentityValidationClients.FullScenarioChatAttempts);
    }

    [Fact]
    public async Task Embedding_attempts_have_their_own_persistent_cap_without_an_unmetered_path()
    {
        using(var clients=Client())
            for(var i=0;i<IdentityValidationClients.CampaignEmbeddingAttempts;i++) await clients.Embeddings.GenerateAsync(["short"]);
        using var restarted=Client();
        var act=async()=>await restarted.Embeddings.GenerateAsync(["not sent"]);
        await act.Should().ThrowAsync<Exception>();
        _hits.Should().Be(IdentityValidationClients.CampaignEmbeddingAttempts); ReadCampaign().Should().HaveCount(IdentityValidationClients.CampaignEmbeddingAttempts);
    }

    [Fact]
    public async Task Failed_http_dispatch_is_reserved_and_sdk_does_not_retry_it_invisibly()
    {
        using var clients=Client(fail:true);
        var act=()=>Ask(clients.Writer); await act.Should().ThrowAsync<Exception>();
        _hits.Should().Be(1); ReadCampaign().Should().ContainSingle();
    }

    [Fact]
    public async Task An_admitted_request_is_recorded_before_transport_so_process_loss_keeps_its_payload()
    {
        using var clients=Client(inspectBeforeDispatch:true);
        await Ask(clients.Writer);
        _requestRecordedBeforeDispatch.Should().Be(_requests.Single(),
            "a process stopped while awaiting the provider must retain the actual capped request for diagnosis");
    }

    [Fact]
    public void A_second_process_cannot_open_the_same_campaign_for_writing()
    {
        using var clients=Client();
        var act=()=>Client(); act.Should().Throw<IOException>(); _hits.Should().Be(0);
    }

    [Fact]
    public async Task Full_scenario_profile_reuses_prior_reservations_instead_of_resetting_the_campaign()
    {
        using(var clients=Client()) for(var i=0;i<60;i++) await Ask(clients.Writer);
        var previous=ReadCampaign().Select(line=>JsonDocument.Parse(line).RootElement.GetProperty("usd").GetDecimal()).Sum();
        using(var full=Client(full:true)) await Ask(full.Writer);
        ReadCampaign().Should().HaveCount(61); _hits.Should().Be(61);
        ReadCampaign().Select(line=>JsonDocument.Parse(line).RootElement.GetProperty("usd").GetDecimal()).Sum()
            .Should().BeGreaterThan(previous).And.BeLessThanOrEqualTo(IdentityValidationClients.CampaignMaximumUsd);
        // One shared campaign: a later four-case client continues the same cumulative ledger, never a fresh one.
        using(var standard=Client()) await Ask(standard.Writer);
        ReadCampaign().Should().HaveCount(62); _hits.Should().Be(62);
    }

    [Fact]
    public async Task Full_scenario_profile_extends_embedding_attempts_without_refunding_prior_usage()
    {
        using(var clients=Client()) for(var i=0;i<180;i++) await clients.Embeddings.GenerateAsync(["short"]);
        using(var full=Client(full:true)) await full.Embeddings.GenerateAsync(["short"]);
        ReadCampaign().Should().HaveCount(181);_hits.Should().Be(181);
    }

    [Fact]
    public async Task Full_profile_still_refuses_dispatch_before_exceeding_the_campaign_maximum()
    {
        using var clients=Client(full:true);
        for(var i=0;i<IdentityValidationClients.FullScenarioChatAttempts && !clients.Exhausted;i++)
        {
            try { await Ask(clients.Writer,new ChatOptions { MaxOutputTokens=4096 },new string('x',28000)); }
            catch(Exception) when(clients.Exhausted) { }
        }
        clients.Exhausted.Should().BeTrue();_hits.Should().BeInRange(61,IdentityValidationClients.FullScenarioChatAttempts-1, "the dollar cap, not the attempt guard, stops it");
        var rows=ReadCampaign();rows.Should().HaveCount(_hits);
        rows.Select(line=>JsonDocument.Parse(line).RootElement.GetProperty("usd").GetDecimal()).Sum()
            .Should().BeLessThanOrEqualTo(IdentityValidationClients.CampaignMaximumUsd);
    }

    private sealed class FakeHandler(Func<HttpRequestMessage,Task<HttpResponseMessage>> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)=>response(request);
    }
    public void Dispose()
    {
        if(Directory.Exists(_folder)) Directory.Delete(_folder,recursive:true);
    }
}
