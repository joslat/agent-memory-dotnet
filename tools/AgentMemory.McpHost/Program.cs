using AgentMemory.McpHost;

// Everything lives in McpHostProgram so the option parsing beside it can be unit-tested; a top-level
// program's members are not reachable from a test project.
return await McpHostProgram.RunAsync(args).ConfigureAwait(false);
