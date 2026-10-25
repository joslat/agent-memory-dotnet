# AgentMemory.Packs.MartialArts

> **Experimental.** A knowledge pack for AgentMemory for .NET (Extensibility 0.x): data only, no code.

"I know kung fu." Not quite, but an agent with this pack knows the grades, techniques and kata of three martial arts.
Combined with what it remembers about you ("I'm 6th kyu in Shotokan"), it can answer "what do I need for my next belt?".

| Type | Records | What it holds |
|---|---|---|
| Art | 3 | Shotokan karate, ITF Taekwon-Do, Judo |
| Rank | 17 | Shotokan 8th to 2nd kyu; ITF 10th to 1st gup; each with its grading requirement and the next grade |
| Technique | 54 | 14 Shotokan stances, blocks, punches, strikes and kicks; the 40 throws of Judo's Gokyo |
| Kata | 19 | 10 Shotokan kata and 9 ITF colour-belt patterns, with move counts, meanings and the rank they belong to |

## Sources

Every record names its source:
- **Shotokan:** the JKA syllabus of the Columbia University JKA club. Grading syllabi differ between organisations
  and dojos (some place Heian Shodan at 10th kyu, others at 7th).
- **ITF Taekwon-Do:** the Chang-Hon colour-belt patterns of General Choi Hong Hi. Gup syllabi differ by federation.
- **Judo:** the Kodokan Gokyo no Waza, 1920 revision. The Gokyo is a teaching syllabus, not a rank requirement.

**Your dojo's syllabus wins.** The pack is reference knowledge for an assistant, not a grading authority.

## Use

```csharp
var trust = new BundleTrust();   // the official key is built in; add developer keys from host configuration only
var pack = KnowledgePack.OpenReferenced("io.agentmemory.packs.martialarts", trust);
services.AddAgentMemoryModules(m => m.AddKnowledgePack(pack).UseNeo4jStore());
services.AddNeo4jKnowledgePacks();
// at start, once per version:
await provider.GetRequiredService<KnowledgePackImporter>().ImportAsync(pack);
```

Records are shared: every owner of the store sees them. A fresh session's question is searched across the pack's
search views, and the retrieval gate judges each match beside core memories.
