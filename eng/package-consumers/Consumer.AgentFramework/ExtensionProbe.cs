using AgentMemory.Abstractions.Options;
using AgentMemory.Abstractions.Services;
using AgentMemory.AgentFramework;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

// This is an ordinary NuGet consumer assembly, without InternalsVisibleTo access.
internal sealed class ExtensionProbe(IServiceProvider services) : Neo4jMemoryContextProvider(
    services.GetRequiredService<IMemoryService>(),
    services.GetRequiredService<IEmbeddingOrchestrator>(),
    services.GetRequiredService<IClock>(),
    services.GetRequiredService<IIdGenerator>(),
    services.GetRequiredService<IOptions<MemoryOptions>>(),
    services.GetRequiredService<IOptions<ContextFormatOptions>>(),
    services.GetRequiredService<IOptions<AgentFrameworkOptions>>(),
    services.GetRequiredService<ILogger<Neo4jMemoryContextProvider>>())
{
    internal void Verify()
    {
        var message = new ChatMessage(ChatRole.User, "question") { MessageId = "consumer-1" };
        if (StoredMessageId(message) != "maf:consumer-1" || message.MessageId != "consumer-1")
            throw new InvalidOperationException("Existing provider ID mapping changed.");
        var withoutId = new ChatMessage(ChatRole.User, "question");
        if (StoredMessageId(withoutId) is not null || withoutId.MessageId is not null)
            throw new InvalidOperationException("ID lookup invented provenance.");
        if (QuestionOf([message, new ChatMessage(ChatRole.Tool, "tool output")]) != "question")
            throw new InvalidOperationException("A trailing tool replaced the user question.");
        if (RenderAdmittedBlock("sample", ["uses <dark> mode"]) !=
            "<recalled_memory category=\"sample\">uses &lt;dark&gt; mode</recalled_memory>")
            throw new InvalidOperationException("Extension content was not safely delimited.");
        if (RenderAdmittedBlock("empty", []).Length != 0)
            throw new InvalidOperationException("Empty extension content changed the prompt.");
        Console.WriteLine("Supported protected extension methods passed without friend access.");
    }
}
