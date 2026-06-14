using System.Reflection;
using AgentMemory.Benchmarks;
using AgentMemory.Benchmarks.Infrastructure;
using BenchmarkDotNet.Running;

// `--smoke` runs every benchmark's setup + body exactly once against the live container and prints the
// result. It validates the whole pipeline (container start, schema bootstrap, seeding, each operation)
// in seconds, without BenchmarkDotNet's statistical machinery — handy in dev/CI-adjacent checks.
if (args.Contains("--smoke"))
{
    try
    {
        await SmokeTest.RunAsync();
        return 0;
    }
    finally
    {
        await BenchmarkNeo4j.ShutdownAsync();
    }
}

// Normal path: hand off to BenchmarkDotNet. The shared Testcontainer is started lazily by the first
// benchmark's GlobalSetup and torn down here once the run (or a filtered subset) completes.
try
{
    BenchmarkSwitcher
        .FromAssembly(Assembly.GetExecutingAssembly())
        .Run(args, new BenchmarkConfig());
    return 0;
}
finally
{
    await BenchmarkNeo4j.ShutdownAsync();
}
