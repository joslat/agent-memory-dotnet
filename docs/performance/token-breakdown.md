# What answering from memory costs (B3)

The most persuasive number in this category is a claim about **architecture**, not model quality. It
cannot be inflated by a better answer model, tuned with prompt engineering, or moved by the judge
changing its mind. It is either true of the assembled context or it is not.

## Measured

Run `longmemeval-prepared-20260812T234837Z`, cold build, **2 questions**, both arms accepted at 100%.
Counted with `Microsoft.ML.Tokenizers` — `CountMethod = Exact`, `Encoding = gpt-4o`. Not an estimate.

| Arm | Context tokens | Full transcript | Compression |
|---|---:|---:|---:|
| Structured | 351 | 98,409 | **280×** |
| Structured | 340 | 100,767 | **296×** |
| Hybrid | 1,067 | 98,409 | **92×** |
| Hybrid | 2,676 | 100,767 | **38×** |

Where the structured arm's budget goes: preferences 179, facts 112, entities 60 — ten items each.
The hybrid arm's cost is dominated by raw messages (904 and 2,520 tokens for 15 messages), which is
the whole point of the comparison: the structured arm answers from extracted memory, the hybrid arm
also carries transcript.

**The baseline is the full transcript** — what a memoryless agent would have to send to answer the
same question — not a truncated window. Comparing against a truncated window would flatter the
result by measuring against a system that has already given up on remembering.

## What this is not

**Two questions is a verification run, not a published measurement.** It proves the instrument works
end to end and establishes an order of magnitude. It does not establish a headline: n=2 says nothing
about variance across question types, and the two transcripts here happen to be nearly the same size.

To publish, run the same command over a frozen 50-question corpus and report the distribution rather
than the mean — a compression ratio is a ratio of two skewed quantities, and its mean is not a
typical case.

## Reproducing

```bash
dotnet run --project tools/AgentMemory.LongMemEval -c Release -- \
  --prepared-pair --questions 50 --description "B3 token breakdown"
```

Every telemetry row carries `TokenBreakdown` with per-section costs, the full-history denominator,
and the counting method. The method travels with the number deliberately: when no tiktoken encoding
resolves for the model, the counter falls back and reports `CharacterHeuristic`, so an estimate can
never be read as a measurement.

## Related

- [Performance baselines](README.md)
- [Bulk ingestion](bulk-ingestion.md)
