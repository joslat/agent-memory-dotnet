using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace AgentMemory.McpHost.Coding;

internal static class CodingToolDefinitions
{
    public static readonly Tool[] All =
    [
        Define("recall", "Recall relevant memory for this project and session.",
            ("session", Text(256), true), ("query", Text(8000), true)),
        Define("remember", "Queue one bounded user turn or deliberate digest with source provenance. Acceptance is not completion; check write_status.",
            ("session", Text(256), true), ("turnId", Text(256), true), ("userText", Text(8000), true),
            ("assistantText", Text(8000), false), ("commit", Text(64), true), ("source", Text(512), true)),
        Define("forget", "Soft-invalidate a memory in this project. Retains its history; never hard-deletes.",
            ("kind", new { type = "string", @enum = new[] { "entity", "fact", "preference" } }, true), ("id", Text(256), true)),
        Define("history", "Read up to 50 recent history records, including closed memories; optionally filter by memory ID.", ("id", Text(256), false)),
        Define("profile", "Read the current owner's profile in this project."),
        Define("write_status", "Read a retained asynchronous write result. Unknown is not success. Job history is not durable.", ("jobId", Text(128), true)),
    ];

    private static readonly Tool[] Durable = All.Select(tool => new Tool
    {
        Name = tool.Name, InputSchema = tool.InputSchema,
        Description = tool.Name switch
        {
            "remember" => "Persist and queue one bounded turn or deliberate digest. A journal receipt survives restart within retention; acceptance is not completion. Check write_status.",
            "write_status" => "Read a retained scoped journal receipt. Unknown means extraction is uncertain and is not automatically replayed; eviction ends receipt availability.",
            _ => tool.Description,
        },
    }).ToArray();

    public static IReadOnlyList<Tool> For(bool durable) => durable ? Durable : All;

    private static object Text(int maximum) => new { type = "string", minLength = 1, maxLength = maximum };

    private static Tool Define(string name, string description, params (string Name, object Schema, bool Required)[] fields) => new()
    {
        Name = name, Description = description,
        InputSchema = JsonSerializer.SerializeToElement(new
        {
            type = "object", additionalProperties = false,
            properties = fields.ToDictionary(f => f.Name, f => f.Schema),
            required = fields.Where(f => f.Required).Select(f => f.Name).ToArray(),
        }),
    };
}
