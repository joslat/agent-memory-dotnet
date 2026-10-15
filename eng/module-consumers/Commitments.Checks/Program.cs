using System.Text.Json;
using AgentMemory.Examples.Commitments;
using AgentMemory.Extensibility.Ingestion;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;
using AgentMemory.Extensibility.Context;

var results = new List<object>(); var failed = 0;
var provider = new CommitmentProvider();
var manifest = ModuleManifest.LoadFromAssemblyFile(typeof(CommitmentProvider).Assembly.Location);
var resolution = ModuleResolver.Resolve([manifest], ModuleVersion.Parse("0.1.0"), new HashSet<string> { "ModuleSchema" });
var isV2 = manifest.Version.Value == "2.0.0";
const string command = """{"format":"agentmemory.commitment/v1","key":"draft","title":"Send Atlas draft","detail":"Explicit teaching","status":"open","priority":2,"due_at":"2026-10-19T16:00:00Z","about":{"kind":"identity","target":"atlas"}}""";
IngestionRequest Request(string text = command, string role = "user") => new() { SessionId = "session", Owner = "alex", Messages = [new() { Id = "persisted-source", Role = role, Text = text }] };
void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
async Task Check(string name, Func<Task> action)
{
    try { await action(); results.Add(new { name, passed = true }); }
    catch (Exception e) { failed++; results.Add(new { name, passed = false, error = e.GetType().Name + ": " + e.Message }); }
}
await Check("embedded-package-metadata", () =>
{
    Require(resolution.Succeeded, string.Join(";", resolution.Errors));
    Require(manifest.Id == "io.agentmemory.examples.commitments", "Wrong identity");
    Require(manifest.Ontology!.Types.Single().Properties.ContainsKey("priority_reason") == isV2, "Wrong version properties");
    Require(manifest.Views.Single(v => v.Id == "history").Selection == ModuleRecordSelection.History, "History selection missing");
    return Task.CompletedTask;
});
await Check("user-command-proposal-and-admission", async () =>
{
    var request = Request(); var proposal = (await provider.ProposeAsync(request)).Single();
    Require(proposal.Key == "draft" && proposal.Type == "CommitmentItem", "Wrong record identity");
    Require(proposal.Source.MessageIds.SequenceEqual(new[] { "persisted-source" }) && request.Messages[0].Text.Contains(proposal.Source.Excerpt, StringComparison.Ordinal), "Fabricated source");
    Require(proposal.Links.Single().Relation == "COMMITMENT_ABOUT" && proposal.Links.Single().Target == "atlas", "Wrong link");
    Require(proposal.Properties["due_at"].GetString() == "2026-10-19T16:00:00Z", "Lost stored value");
    Require(ModuleProposalAdmission.Admit(resolution, provider.Descriptor, request, proposal).Accepted, "SDK refused valid proposal");
});
await Check("assistant-and-ordinary-text-do-not-teach", async () =>
{
    Require((await provider.ProposeAsync(Request(command, "assistant"))).Count == 0, "Assistant created commitment");
    Require((await provider.ProposeAsync(Request("Maybe we should do something later"))).Count == 0, "Inferred a commitment");
});
foreach (var status in new[] { "completed", "cancelled" })
    await Check(status + "-is-versioned-state-not-invalidation", async () =>
    {
        var request = Request(command.Replace("\"open\"", "\"" + status + "\"", StringComparison.Ordinal));
        var proposal = (await provider.ProposeAsync(request)).Single();
        Require(proposal.Kind == ProposalKind.Assert && proposal.Properties["status"].GetString() == status, "Wrong closure operation");
    });
await Check("name-target-is-explicit", async () =>
{
    var proposal = (await provider.ProposeAsync(Request(command.Replace("\"identity\"", "\"name\"", StringComparison.Ordinal)))).Single();
    Require(proposal.Links.Single().TargetKind == LinkTargetKind.EntityName, "Lost explicit name mode");
});
foreach (var (name, text) in new[] {
    ("duplicate-format", command.Replace("\"key\":", "\"format\":\"other\",\"key\":", StringComparison.Ordinal)),
    ("unknown-field", command.Replace("\"key\":", "\"owner\":\"other\",\"key\":", StringComparison.Ordinal)),
    ("duplicate-field", command.Replace("\"priority\":2", "\"priority\":2,\"priority\":3", StringComparison.Ordinal)),
    ("malformed-about", command.Replace("\"kind\":\"identity\"", "\"kind\":\"fuzzy\"", StringComparison.Ordinal)) })
    await Check(name + "-refused", async () =>
    {
        try { await provider.ProposeAsync(Request(text)); }
        catch (FormatException) { return; }
        throw new InvalidOperationException("Malformed teaching was not refused");
    });
await Check("version-specific-priority-reason", async () =>
{
    var text = command.Replace("\"priority\":2", "\"priority\":2,\"priority_reason\":\"Time-sensitive\"", StringComparison.Ordinal);
    try
    {
        var request = Request(text); var proposal = (await provider.ProposeAsync(request)).Single();
        Require(isV2 && ModuleProposalAdmission.Admit(resolution, provider.Descriptor, request, proposal).Accepted, "Wrong version admission");
        Require(proposal.Properties["priority_reason"].GetString() == "Time-sensitive", "Reason was lost");
    }
    catch (FormatException) when (!isV2) { }
});
await Check("cancellation", async () =>
{
    using var cts = new CancellationTokenSource(); cts.Cancel();
    try { await provider.ProposeAsync(Request(), cts.Token); }
    catch (OperationCanceledException) { return; }
    throw new InvalidOperationException("Cancellation ignored");
});
await Check("optional-fields-and-bounds-use-sdk-admission", async () =>
{
    var root = System.Text.Json.Nodes.JsonNode.Parse(command)!; root.AsObject().Remove("due_at"); root.AsObject().Remove("detail");
    var request = Request(root.ToJsonString()); var proposal = (await provider.ProposeAsync(request)).Single();
    Require(!proposal.Properties.ContainsKey("due_at") && !proposal.Properties.ContainsKey("priority_reason"), "Invented optional state");
    Require(ModuleProposalAdmission.Admit(resolution, provider.Descriptor, request, proposal).Accepted, "Absent optional field refused");
    foreach (var mutation in new[] { "status", "priority", "title" })
    {
        root = System.Text.Json.Nodes.JsonNode.Parse(command)!;
        root[mutation] = mutation switch { "priority" => System.Text.Json.Nodes.JsonValue.Create(6), "title" => System.Text.Json.Nodes.JsonValue.Create(new string('x', 201)), _ => System.Text.Json.Nodes.JsonValue.Create("assumed-done") };
        request = Request(root.ToJsonString()); proposal = (await provider.ProposeAsync(request)).Single();
        Require(!ModuleProposalAdmission.Admit(resolution, provider.Descriptor, request, proposal).Accepted, "SDK admitted invalid " + mutation);
    }
});
await Check("bounded-source-excerpt-is-an-exact-substring", async () =>
{
    var root = System.Text.Json.Nodes.JsonNode.Parse(command)!; root["detail"] = new string('x', 1000);
    var request = Request(root.ToJsonString()); var proposal = (await provider.ProposeAsync(request)).Single();
    Require(proposal.Source.Excerpt.Length <= 500 && request.Messages[0].Text.Contains(proposal.Source.Excerpt, StringComparison.Ordinal), "Bad bounded excerpt");
});
await Check("leading-whitespace-does-not-destroy-source-provenance", async () =>
{
    var request = Request(new string(' ', 600) + command); var proposal = (await provider.ProposeAsync(request)).Single();
    Require(request.Messages[0].Text.Contains(proposal.Source.Excerpt, StringComparison.Ordinal), "Invented excerpt");
    Require(ModuleProposalAdmission.Admit(resolution, provider.Descriptor, request, proposal).Accepted, "Valid whitespace-prefixed command lost provenance");
});
await Check("oversized-teaching-is-refused", async () =>
{
    var root = System.Text.Json.Nodes.JsonNode.Parse(command)!; root["detail"] = new string('x', 17000);
    try { await provider.ProposeAsync(Request(root.ToJsonString())); }
    catch (FormatException) { return; }
    throw new InvalidOperationException("Unbounded teaching accepted");
});
await Check("package-contributors-reuse-shared-target-and-history-views", async () =>
{
    var reader = new ProbeReader(); var request = new ContextRequest { SessionId = "fresh", Owner = "alex", Query = "atlas" };
    var contributor = CommitmentsModule.ForEntity(resolution, reader, r => r.Query);
    Require((await contributor.ContributeResultAsync(request)).OmissionReason == ContextOmissionReason.SearchedAndEmpty, "Wrong empty disposition");
    Require(reader.Query!.Links.Single().TargetId == "atlas" && reader.Query.Where.Single().Value.GetString() == "open", "Package bypassed entity view");
    Require(reader.Context!.Scope!.OwnerId == "alex" && reader.Context.SessionId == "fresh", "Lost read context");
    await CommitmentsModule.History(resolution, reader, _ => "draft").ContributeResultAsync(request);
    Require(reader.Query!.Selection == ModuleRecordSelection.History && reader.Query.Key == "draft", "Package bypassed history view");
    await CommitmentsModule.Due(resolution, reader).ContributeResultAsync(request);
    Require(reader.Query!.Where.Any(f => f.Value.ValueKind == JsonValueKind.String && f.Value.GetString() == "$now"), "Package bypassed due view");
    var calls = reader.Calls;
    var skipped = await CommitmentsModule.ForEntity(resolution, reader, _ => null).ContributeResultAsync(request);
    Require(reader.Calls == calls && skipped.OmissionReason == ContextOmissionReason.NotApplicable, "Absent entity queried every commitment");
});
Console.WriteLine(JsonSerializer.Serialize(new { packageVersion = manifest.Version.Value, framework = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
    assembly = typeof(CommitmentProvider).Assembly.Location, sdk = typeof(ModuleManifest).Assembly.Location, passed = results.Count - failed, failed, checks = results }));
return failed == 0 ? 0 : 1;


sealed class ProbeReader : IModuleReader
{
    public string CallerModuleId => CommitmentsModule.Id;
    public ModuleQuery? Query { get; private set; }
    public ModuleReadContext? Context { get; private set; }
    public int Calls { get; private set; }
    public Task<ModuleReadResult> FindAsync(ModuleQuery query, ModuleReadContext context, CancellationToken cancellationToken = default)
    { Calls++; Query = query; Context = context; return Task.FromResult(new ModuleReadResult { Status = ModuleReadStatus.Succeeded }); }
}
