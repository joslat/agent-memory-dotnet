using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentMemory.Inference;
using Microsoft.Extensions.AI;
using OpenAI;

namespace AgentMemory.Scenarios;

/// <summary>A small, fixed-provider acceptance campaign. Every actual inference HTTP attempt is metered.</summary>
internal sealed class IdentityValidationClients : IDisposable
{
    /// <summary>
    /// Cumulative campaign guards, shared by the full and four-case profiles. History: USD 1 with 200/800 (full) and 60/180
    /// (four cases) until 2026-11-13; full chat 260 that night; then, on the owner's approval of the budget ("all budget
    /// approved ... be conservative"), USD 2 and one shared 500 chat / 1,600 embedding guard; then, for the standing
    /// benchmark reruns of person continuity (owner: "all approved"), USD 3 with 800 chat / 2,400 embedding; USD 4 for
    /// the busy variant; USD 5.5 (2,000 / 6,000) after the old-model busy run hit USD 4 mid-run, for its rerun and the
    /// ablations; USD 6.5 (2,200 / 7,800; the attempt guard must bind before the dollar cap) for the repeat of the busy head-to-head; USD 10.5
    /// (2,700 / 7,800) for the independent identity gold (ILC-23, owner "go for it"; about USD 4 planned); USD 12 (3,100 /
    /// 7,800) for the people-recall head-to-head (ILC-33, owner "all of it": two old and two new busy runs, about USD 2.4
    /// measured from earlier busy runs at 0.50 old and 0.69 new). The ledger is never reset.
    /// </summary>
    internal const int FullScenarioChatAttempts=3100; // stays below the dollar cap even at full-size requests
    internal const int CampaignEmbeddingAttempts=7800;
    internal const decimal CampaignMaximumUsd=12m;

    internal const string Endpoint = "https://api-inference.bitdeer.ai/v1";
    internal const string ChatModel = "zai-org/GLM-5.3-Flash";
    internal const string EmbeddingModel = "BAAI/bge-m3";
    private readonly CampaignBudget _budget;
    private readonly List<HttpClient> _http = [];
    public IChatClient Writer { get; }
    public IChatClient Answer { get; }
    public IChatClient Adjudicator { get; }
    public IEmbeddingGenerator<string,Embedding<float>> Embeddings { get; }

    public bool Exhausted => _budget.Exhausted;
    public object Summary => _budget.Summary;

    public static object Plan => PlanFor(false);
    public static object FullScenarioPlan => PlanFor(true);
    internal static object PlanFor(bool fullScenario, string? profile = null) => new
    {
        kind="budget_execution_plan",profile=profile ?? (fullScenario?"bariloche-49":"identity-four-cases"), endpoint=Endpoint,chatModel=ChatModel,embeddingModel=EmbeddingModel,
        roles=new[] { "selected-writer-or-extractor-with-chat-update-fallback","identity-adjudication","answer","embedding" },
        extractionPath="Selected bounded arm, recorded as effectiveIngestion in the execution plan", maxCandidates=4, maxAdjudicationsPerIngestion=2, adjudicatorOutputTokens=1024, contextualConfirmationTypes=new[] { "EVENT", "PERSON", "ORGANIZATION" },
        maximumCampaignUsd=CampaignMaximumUsd,maximumChatHttpAttempts=FullScenarioChatAttempts,maximumEmbeddingHttpAttempts=CampaignEmbeddingAttempts,
        maximumRequestBytes=32768,maximumOutputTokens=4096,reasoningEffort="low",httpTimeoutSeconds=45,sdkRetries=0,
        accounting="Persistently reserve worst-case cost before dispatch; no refunds on unknown outcomes",
        inputTokenBound="UTF8 request bytes plus 4096 framing tokens; text-only requests",
        listPricesUsdPerMillion=new { chatInput=0.15m,chatOutput=0.50m,embeddingInput=0.01m },
        priceChecked="2026-11-11",
        sources=new[] { "https://www.bitdeer.ai/en/services/ai-inference/mdl-da9jcesfhomc73a6j740",
            "https://www.bitdeer.ai/en/services/ai-inference/mdl-da4tifpllsks739jr5gg" }
    };

    public IdentityValidationClients(InferenceProviderSettings settings,string campaignPath,string callsPath,Func<string,HttpMessageHandler>? transportFactory=null,bool writerMode=false,bool fullScenario=false)
    {
        if(settings.Provider!=InferenceProvider.Bitdeer || settings.Endpoint.TrimEnd('/')!=Endpoint ||
            settings.Model!=ChatModel || settings.EffectiveExtractionModel!=ChatModel || settings.EmbeddingModel!=EmbeddingModel ||
            settings.EmbeddingEndpoint is not null && settings.EmbeddingEndpoint.TrimEnd('/')!=Endpoint ||
            settings.EmbeddingProvider is not (InferenceProvider.None or InferenceProvider.Bitdeer))
            throw new InvalidOperationException("Bounded identity validation requires the explicitly planned Bitdeer models and endpoint.");
        if(fullScenario && !writerMode) throw new ArgumentException("The full Bariloche profile requires the store-aware writer.");
        _budget=new CampaignBudget(campaignPath,callsPath,fullScenario);
        OpenAIClient Client(string role)
        {
            var credential=role=="embedding"?settings.EmbeddingApiKey??settings.ApiKey:settings.ApiKey;
            var http=new HttpClient(new BudgetHandler(_budget,role,credential)
            { InnerHandler=transportFactory?.Invoke(role) ?? new SocketsHttpHandler { AllowAutoRedirect=false } }) { Timeout=TimeSpan.FromSeconds(45) };
            _http.Add(http);
            return new(new ApiKeyCredential(credential),new OpenAIClientOptions
            { Endpoint=new Uri(Endpoint),NetworkTimeout=TimeSpan.FromSeconds(45),RetryPolicy=new ClientRetryPolicy(0),
                Transport=new HttpClientPipelineTransport(http) });
        }
        Writer=Client(writerMode?"store-aware-writer-and-update-fallback":"unified-extraction").GetChatClient(ChatModel).AsIChatClient();
        Answer=Client("answer").GetChatClient(ChatModel).AsIChatClient();
        Adjudicator=Client("identity-adjudication").GetChatClient(ChatModel).AsIChatClient();
        Embeddings=Client("embedding").GetEmbeddingClient(EmbeddingModel).AsIEmbeddingGenerator();
    }

    public void Dispose()
    {
        Writer.Dispose(); Answer.Dispose(); Adjudicator.Dispose(); Embeddings.Dispose();
        foreach(var http in _http) http.Dispose();
        _budget.Dispose();
    }

    private sealed class BudgetHandler(CampaignBudget budget,string role,string apiKey) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken cancellationToken)
        {
            var bytes=request.Content is null?[]:await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if(bytes.Length is 0 or >32768 || request.Method!=HttpMethod.Post ||
                request.RequestUri is not { Scheme:"https",Host:"api-inference.bitdeer.ai",IsDefaultPort:true } uri ||
                !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo) ||
                uri.AbsolutePath is not ("/v1/chat/completions" or "/v1/embeddings"))
                throw new InvalidOperationException("An unplanned inference route or oversize request was refused before dispatch.");
            var embedding=uri.AbsolutePath=="/v1/embeddings";
            if(!embedding)
            {
                var planned=JsonNode.Parse(bytes)!.AsObject();
                if(!planned.ContainsKey("max_completion_tokens") && !planned.ContainsKey("max_tokens"))
                {
                    planned["max_tokens"]=4096;
                }
                // This fixed reasoning model otherwise defaults to maximum thinking, which can
                // consume the whole cap without producing JSON. Keep the cap, limit the effort.
                planned["reasoning_effort"]="low";
                bytes=JsonSerializer.SerializeToUtf8Bytes(planned);
                if(bytes.Length>32768) throw new InvalidOperationException("The capped request exceeds its input budget.");
                request.Content=new ByteArrayContent(bytes);
                request.Content.Headers.ContentType=new("application/json");
            }
            using var document=JsonDocument.Parse(bytes);
            var root=document.RootElement;
            if(root.GetProperty("model").GetString()!=(embedding?EmbeddingModel:ChatModel))
                throw new InvalidOperationException("An unplanned model was refused before dispatch.");
            var output=0;
            if(!embedding)
            {
                if(root.TryGetProperty("stream",out var streaming) && streaming.ValueKind==JsonValueKind.True)
                    throw new InvalidOperationException("This validation records whole responses and does not stream.");
                if(!root.TryGetProperty("max_completion_tokens",out var cap) && !root.TryGetProperty("max_tokens",out cap) ||
                    !cap.TryGetInt32(out output) || output is <1 or >4096)
                    throw new InvalidOperationException("A missing or unplanned output-token cap was refused before dispatch.");
                foreach(var message in root.GetProperty("messages").EnumerateArray())
                    if(message.TryGetProperty("content",out var content) && content.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) &&
                        (content.ValueKind!=JsonValueKind.Array || content.EnumerateArray().Any(part=>
                            !part.TryGetProperty("type",out var type) || type.GetString()!="text" ||
                            !part.TryGetProperty("text",out var value) || value.ValueKind!=JsonValueKind.String)))
                        throw new InvalidOperationException("Only planned text input can use this cost bound.");
            }
            var reserved=budget.Reserve(role,embedding,bytes.Length,output);
            var started=System.Diagnostics.Stopwatch.StartNew();
            string Redact(string value)=>value.Replace(apiKey,"[REDACTED]",StringComparison.Ordinal);
            // Keep the actual capped request before awaiting the provider: an interrupted process
            // may never reach either completion branch, while its durable cost reservation remains.
            budget.Record(new { phase="admitted",role,attempt=reserved.Attempt,reservedUsd=reserved.Usd,
                requestedOutputTokens=output,atUtc=DateTimeOffset.UtcNow,request=Redact(Encoding.UTF8.GetString(bytes)) });
            try
            {
                var response=await base.SendAsync(request,cancellationToken).ConfigureAwait(false);
                var body=await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                budget.Record(new { phase="completed",role,attempt=reserved.Attempt,reservedUsd=reserved.Usd,ms=started.ElapsedMilliseconds,
                    status=(int)response.StatusCode,request=Redact(Encoding.UTF8.GetString(bytes)),response=Redact(body) });
                return response;
            }
            catch(Exception error)
            {
                budget.Record(new { phase="failed",role,attempt=reserved.Attempt,reservedUsd=reserved.Usd,ms=started.ElapsedMilliseconds,
                    error=error.GetType().Name,request=Redact(Encoding.UTF8.GetString(bytes)) });
                throw;
            }
        }
    }

    private sealed class CampaignBudget : IDisposable
    {
        private readonly FileStream _writer;
        private readonly FileStream _lock;
        private readonly StreamWriter _campaign;
        private readonly StreamWriter _calls;
        private readonly object _gate=new();
        private decimal _reserved;
        private int _chat,_embedding;
        private readonly int _maximumChat,_maximumEmbedding;

        public CampaignBudget(string campaignPath,string callsPath,bool fullScenario)
        {
            _maximumChat=FullScenarioChatAttempts;
            _maximumEmbedding=CampaignEmbeddingAttempts;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(campaignPath))!);
            // One writer per campaign on every platform. FileShare.None is an exclusive lock on Windows and on Unix (an
            // exclusive flock); the campaign file itself stays readable (FileShare.Read), which on Unix is only a shared
            // lock and let a second process open it for writing (found by CI on Linux).
            _writer=new FileStream(campaignPath+".writer",FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);
            _lock=new FileStream(campaignPath,FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.Read);
            using(var reader=new StreamReader(_lock,Encoding.UTF8,leaveOpen:true))
            {
                while(reader.ReadLine() is { } line)
                {
                    using var entry=JsonDocument.Parse(line);
                    var root=entry.RootElement;
                    if(root.GetProperty("version").GetString()!="identity-budget/1") throw new InvalidDataException("Unknown campaign budget format.");
                    var cost=root.GetProperty("usd").GetDecimal();
                    if(cost is <=0 or >1) throw new InvalidDataException("Invalid persisted inference reservation.");
                    _reserved+=cost;
                    if(root.GetProperty("embedding").GetBoolean()) _embedding++; else _chat++;
                }
            }
            _lock.Seek(0,SeekOrigin.End);
            _campaign=new StreamWriter(_lock,new UTF8Encoding(false),leaveOpen:true) { AutoFlush=true };
            _calls=new StreamWriter(callsPath,append:false) { AutoFlush=true };
        }

        public bool Exhausted { get; private set; }
        public object Summary { get { lock(_gate) return new { reservedUsd=_reserved,maximumUsd=CampaignMaximumUsd,
            chatHttpAttempts=_chat,embeddingHttpAttempts=_embedding,exhausted=Exhausted,
            billedCost="Not an account invoice; conservative reservations at the recorded published list rates" }; } }

        public (int Attempt,decimal Usd) Reserve(string role,bool embedding,int requestBytes,int outputTokens)
        {
            lock(_gate)
            {
                var inputBound=requestBytes+4096;
                var usd=(inputBound*(embedding?0.01m:0.15m)+outputTokens*0.50m)/1_000_000m;
                if(_reserved+usd>CampaignMaximumUsd || (embedding?_embedding>=_maximumEmbedding:_chat>=_maximumChat))
                {
                    Exhausted=true;
                    throw new InvalidOperationException("The persistent identity campaign reached its inference budget; no request was sent.");
                }
                _campaign.WriteLine(JsonSerializer.Serialize(new { version="identity-budget/1",role,embedding,usd,
                    requestBytes,inputTokenBound=inputBound,outputTokens,atUtc=DateTimeOffset.UtcNow }));
                _lock.Flush(flushToDisk:true); // a lost reply or process restart cannot silently reuse this reservation
                _reserved+=usd;
                if(embedding) _embedding++; else _chat++;
                return(_chat+_embedding,usd);
            }
        }

        public void Record(object entry)
        {
            lock(_gate)
            {
                _calls.WriteLine(JsonSerializer.Serialize(entry));
                if(_calls.BaseStream is FileStream file) file.Flush(flushToDisk:true);
            }
        }
        public void Dispose() { _calls.Dispose(); _campaign.Dispose(); _lock.Dispose(); _writer.Dispose(); }
    }
}
