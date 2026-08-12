# Bulk ingestion

Loading a backlog — a year of transcripts, an export from another system, a migration — is a
different problem from ingesting a live turn, and the two obvious ways to do it are both wrong.

A serial loop over `ExtractAndPersistAsync` is correct and takes hours: extraction is a model call,
and one at a time means one round trip at a time.

An unbounded `Parallel.ForEachAsync` is fast until it isn't. It saturates the provider quota and the
Neo4j connection pool, and the damage is not to the bulk load — it is to **everything else sharing
the process**. Median latency barely moves, which is what makes it hard to notice; p99 degrades by
20–70% under saturation, so the tenants who suffer are the ones already having the worst time.

## The documented path

```csharp
var result = await memory.IngestBulkAsync(
    requests,
    new BulkIngestionOptions { MaxConcurrency = 4 },
    cancellationToken);

Console.WriteLine($"{result.SucceededCount} ingested, {result.FailedCount} failed, " +
                  $"{result.NotAttemptedCount} not attempted");

foreach (var failure in result.Outcomes.Where(o => !o.Succeeded))
    Console.WriteLine($"  request {failure.Index}: {failure.Error!.Message}");
```

`IngestBulkAsync` is a **default interface method on `IMemoryIngestion`**. It paces calls the host
could have made itself; it is not a second ingestion pipeline. That distinction matters more than it
sounds: a separate bulk path would be a second place for trust stamping, provenance and owner scoping
to drift out of agreement with the per-request path, and the drift would only show up in the corpus
months later.

## What the options actually buy

| Option | Default | Why |
|---|---|---|
| `MaxConcurrency` | `4` | High enough to beat a serial loop, low enough that a caller who never tunes it does not saturate a shared quota by accident. Bounding this does not make the bulk load faster — it stops the bulk load making everything else slower. |
| `ContinueOnError` | `true` | In a ten-thousand-conversation load, one malformed transcript aborting the other 9,999 is rarely what was wanted. Continuing is not ignoring: every failure comes back in `Outcomes`. |

## Failures are reported per request, not counted

A bulk API that returns *"8,412 of 10,000 succeeded"* tells you that you have a problem and nothing
about which inputs to retry — so the realistic response is to re-run all ten thousand, which costs
more than the failure did. Every outcome carries its `Index` into the submitted list and the
`Exception` that stopped it.

`NotAttemptedCount` is kept separate from `FailedCount` on purpose. **"Tried and failed" and "never
tried" call for different actions**: re-running a request that was never attempted is always safe,
re-running one that failed part-way may not be. Collapsing them makes a stopped run look like a
partially broken corpus.

## Cancellation

A run stopped by `ContinueOnError = false` **completes normally** and returns its report — the
failure that stopped it is already in `Outcomes`, and throwing would discard the record of what did
succeed.

A cancellation *you* requested throws `OperationCanceledException`. That is a different event and is
not swallowed into a tidy report.

## What this is not

This is not the 10.70× figure from the throughput analysis. That number is ten-owner concurrent
throughput, and it has always been available to any caller willing to parallelise across owners —
it is a property of the store, not of this API. What is documented here is the safe way to do the
parallelising, which is a smaller and more honest claim.

## Related

- [Performance baselines](README.md)
- Extraction batching (`MaxConcurrentExtractionBatches`) bounds concurrency *inside* one extraction;
  this bounds concurrency *across* requests. They compose, and multiply — a `MaxConcurrency` of 4
  over batches of 6 is 24 in-flight provider calls.
