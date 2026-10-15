using System.Text.Json;

namespace AgentMemory.McpHost.Coding.Client;

internal static class CodingHookCommand
{
    public static async Task<int> RunAsync(string[] args, Stream input, TextWriter output,
        Func<string, string?> environment, CancellationToken cancellationToken = default)
    {
        try
        {
            var transform = args.Length == 4 && args[2] == "--event" && args[3] == "userPromptTransformed";
            if ((args.Length != 2 && !transform) || args[0] != "--coding-hook") throw new ArgumentException("Invalid hook arguments.");
            var json = await CodingJson.ReadConfigurationAsync(args[1], cancellationToken).ConfigureAwait(false);
            var options = CodingJson.ParseConfiguration<CodingHookOptions>(json);
            options.Validate();
            if (transform && options.Harness != CodingHookHarness.CopilotCli)
                throw new ArgumentException("Prompt transformation requires the Copilot CLI harness.");
            var token = environment(options.TokenVariable);
            // Reuse the service's credential requirements rather than defining a second token format.
            _ = new CodingProjectAccess(new CodingServiceOptions
            {
                OwnerId = "client", Projects = new Dictionary<string, string> { [options.Project] = options.TokenVariable }, Url = options.ServiceUrl,
            }, environment);
            var secrets = options.SecretVariables.Select(environment).Where(v => v is not null).Cast<string>().Append(token!);
            var policy = new CodingContentPolicy(secrets);
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.TimeoutMilliseconds);
            var bytes = new byte[CodingBodyLimit.MaximumBytes + 1];
            var count = await input.ReadAtLeastAsync(bytes, bytes.Length, false, deadline.Token).ConfigureAwait(false);
            if (count > CodingBodyLimit.MaximumBytes) throw new ArgumentException("Oversized hook input.");
            using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 16 });
            using var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
            using var client = new HttpClient(handler) { BaseAddress = new Uri(options.ServiceUrl), Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            var repository = new CodingRepository(options.RepositoryRoot);
            var reply = transform
                ? await new CodingCopilotPromptHook(options, policy, client, repository.ReadCommitAsync)
                    .HandleAsync(document.RootElement, deadline.Token).ConfigureAwait(false)
                : options.Harness is CodingHookHarness.Claude or CodingHookHarness.Codex
                ? await new CodingTurnHook(options, policy, new(options), client, repository.ReadCommitAsync)
                    .HandleAsync(document.RootElement, deadline.Token).ConfigureAwait(false)
                : await new CodingSessionStartHook(options, policy, client, repository.ReadCommitAsync)
                    .HandleAsync(document.RootElement, deadline.Token).ConfigureAwait(false);
            await output.WriteAsync(JsonSerializer.Serialize(reply, CodingApi.Json)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Hooks must not block the coding agent or echo configuration, prompt or provider exceptions.
            await output.WriteAsync("{\"systemMessage\":\"Memory hook skipped: input, repository, state or local service was unavailable. No completed write is claimed.\"}").ConfigureAwait(false);
        }
        return 0;
    }
}
