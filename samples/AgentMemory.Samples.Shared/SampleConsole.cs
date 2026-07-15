namespace AgentMemory.Samples.Shared;

/// <summary>
/// Color-coded console output shared by the samples: the simulated user, the agent's reply, and
/// memory actions (recalled context + memory-tool calls) each get a distinct, high-contrast color so a
/// live run is easy to follow at a glance.
/// </summary>
public static class SampleConsole
{
    /// <summary>Tool names exposed by <c>MemoryToolFactory.CreateAIFunctions()</c> — colored as memory actions.</summary>
    public static readonly HashSet<string> MemoryToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "search_memory", "remember_preference", "remember_fact",
        "recall_preferences", "search_knowledge", "find_similar_tasks",
    };

    public static void WriteUser(string message) => WriteLine(ConsoleColor.Yellow, $"USER      : {message}");

    public static void WriteAssistant(string? message) => WriteLine(ConsoleColor.Green, $"ASSISTANT : {message}\n");

    /// <summary>Prints one tool-call trace line — memory tools in light blue, everything else neutral.</summary>
    public static void WriteToolCall(string name, string preview) =>
        WriteLine(MemoryToolNames.Contains(name) ? ConsoleColor.Cyan : ConsoleColor.DarkGray,
            $"            {name} → {preview}");

    /// <summary>Prints one `&lt;recalled_memory&gt;` block the context provider injected before the model call.</summary>
    public static void WriteRecalledMemory(string category, string body) =>
        WriteLine(ConsoleColor.Cyan, $"          [memory recalled: {category}] {body}");

    private static void WriteLine(ConsoleColor color, string text)
    {
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ResetColor();
    }
}
