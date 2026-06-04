---
name: "aspire-demo-verification"
description: "Verify a minimal Aspire AppHost + Neo4j sample on this repo without touching src/"
domain: "samples"
confidence: "high"
source: "earned during Aspire Demo Task 3 verification on loop/aspire-demo"
---

## Context

This repo's Aspire demo is intentionally sample-only. The executable path is a manually scaffolded AppHost plus a console DemoApp under `samples/AspireDemo/`, with no `src/` changes allowed during verification.

## Pattern

1. Verify the exact plan commands first:
   - `dotnet restore samples\samples.sln`
   - `dotnet build samples\samples.sln`
   - `dotnet run --project samples\AspireDemo\AspireDemo.AppHost\AspireDemo.AppHost.csproj`
   - `dotnet build AgentMemory.slnx`
   - `dotnet test AgentMemory.slnx --filter "FullyQualifiedName!~AgentMemory.Tests.Integration"`
   - `dotnet test AgentMemory.slnx`
2. When AppHost is running, confirm Neo4j Browser with `http://localhost:7474` instead of relying on dashboard output.
3. If needed, verify the seeded graph directly with `docker exec <neo4j-container> cypher-shell -u neo4j -p password ...`.
4. Run `dotnet run --project samples\AspireDemo\AspireDemo.DemoApp\AspireDemo.DemoApp.csproj` separately to capture the scripted recall/context output cleanly.

## Key implementation details to remember

- `AspireDemo.AppHost` needs the `Aspire.Hosting.AppHost` package and launch settings so `dotnet run --project ...AppHost.csproj` works on this machine.
- Fixed host ports depend on the AppHost endpoints being configured for direct host exposure; otherwise Neo4j ends up behind random Aspire proxy ports and the DemoApp cannot use `bolt://localhost:7687`.
- The DemoApp needs sample-local no-op registrations for `IGraphRagContextSource` and `IMemoryExtractionPipeline` so Core services resolve under the minimal sample wiring.

## Anti-patterns

- Do not add the sample projects to `AgentMemory.slnx`.
- Do not "fix" the sample by editing `src/` when the issue is AppHost/sample wiring.
- Do not treat the filtered test-command warning as a sample regression; it is a consequence of the exact filter string used in the plan.
