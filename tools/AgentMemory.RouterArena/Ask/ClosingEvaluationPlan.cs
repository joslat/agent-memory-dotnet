using System.Security.Cryptography;
using System.Text;
using AgentMemory.Abstractions.Services;
using AgentMemory.Gate;

namespace AgentMemory.RouterArena.Ask;

internal sealed record ClosingCase
{
    public required string Id { get; init; }
    public required string Source { get; init; }
    public required string Kind { get; init; }
    public required string Said { get; init; }
    public required string StoredMemory { get; init; }
    public required string NewMemory { get; init; }
    public required bool ShouldClose { get; init; }
    public required string Rationale { get; init; }
}

internal sealed record ClosingDataset
{
    public required int SchemaVersion { get; init; }
    public required string Purpose { get; init; }
    public required DateTimeOffset NowUtc { get; init; }
    public required IReadOnlyList<ClosingCase> Cases { get; init; }
}

internal sealed record ClosingConfiguration
{
    public required SystemOneEndpoint Judge { get; init; }
    public required TimeSpan Timeout { get; init; }
}

internal sealed record ClosingRequest(string Id, MemoryUpdateRequest Request);
internal sealed record ClosingCaseBinding(string Id, string ProposalId, string BaselineRequestId, string CandidateRequestId);
internal sealed record ClosingEvaluationPlan(IReadOnlyList<ClosingRequest> Requests, IReadOnlyList<ClosingCaseBinding> Cases)
{
    public static ClosingEvaluationPlan Build(ClosingDataset dataset, ClosingConfiguration configuration, int? limit)
    {
        if (dataset.SchemaVersion != 1 || string.IsNullOrWhiteSpace(dataset.Purpose) || dataset.NowUtc == default ||
            dataset.Cases is not { Count: > 0 and <= 10000 } || limit is <= 0 ||
            dataset.Cases.Any(c => c is null) || dataset.Cases.Select(c => c.Id).Distinct(StringComparer.Ordinal).Count() != dataset.Cases.Count)
            throw new FormatException("Invalid dataset header or case IDs.");
        var endpoint = configuration.Judge;
        if (endpoint is null || string.IsNullOrWhiteSpace(endpoint.Model) || string.IsNullOrWhiteSpace(endpoint.Name) ||
            endpoint.Endpoint is not { IsAbsoluteUri: true } uri || uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            endpoint.KeyVariable is "OPENAI_API_KEY" || configuration.Timeout <= TimeSpan.Zero || configuration.Timeout > TimeSpan.FromMinutes(1))
            throw new FormatException("Invalid explicit judge configuration.");
        foreach (var item in dataset.Cases)
        {
            if (string.IsNullOrWhiteSpace(item.Id) || item.Id.Length > 256 ||
                item.Source is not ("writer-named" or "similarity") || item.Kind is not ("fact" or "preference" or "relationship") ||
                (item.Kind == "relationship" && item.Source != "writer-named") ||
                !Text(item.Said) || !Text(item.StoredMemory) || !Text(item.NewMemory) || !Text(item.Rationale))
                throw new FormatException("Invalid or unbounded case.");
        }

        var requests = new Dictionary<string, ClosingRequest>(StringComparer.Ordinal);
        var labels = new Dictionary<string, bool>(StringComparer.Ordinal);
        var cases = new List<ClosingCaseBinding>();
        // Validate all labels, even when the pilot selects a prefix.
        foreach (var item in dataset.Cases)
        {
            var common = Request(item, named: true);
            var proposal = Fingerprint(common);
            if (labels.TryGetValue(proposal, out var expected) && expected != item.ShouldClose)
                throw new FormatException("Identical proposals have conflicting labels.");
            labels[proposal] = item.ShouldClose;
        }
        foreach (var item in dataset.Cases.Take(limit ?? dataset.Cases.Count))
        {
            var common = Request(item, named: true);
            var candidateId = Add(common);
            var baselineId = item.Source == "writer-named" ? candidateId : Add(Request(item, named: false));
            cases.Add(new(item.Id, candidateId, baselineId, candidateId));
        }
        return new(requests.Values.ToList(), cases);

        MemoryUpdateRequest Request(ClosingCase item, bool named) => new(item.Said, dataset.NowUtc,
            [new MemoryUpdatePair("p1", item.NewMemory, item.StoredMemory)]) { Named = named };
        string Fingerprint(MemoryUpdateRequest request) => Hash(Encoding.UTF8.GetBytes(SystemOneClient.Render(endpoint.Model,
            SystemOneUpdateJudge.State(request), SystemOneUpdateJudge.Questions(request))));
        string Add(MemoryUpdateRequest request)
        {
            var id = Fingerprint(request);
            requests.TryAdd(id, new(id, request));
            return id;
        }
    }

    private static bool Text(string? text) => !string.IsNullOrWhiteSpace(text) && text.Length <= 8192;
    internal static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
}
