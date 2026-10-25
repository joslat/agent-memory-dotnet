# Repository Lessons (experimental example)

A model-backed memory package that keeps a software repository's lessons: a failure signature and the fix
that resolved it, a convention or constraint of the repository, or a pitfall that was tried and did not
work. It is the model-backed validation module of Extensibility 0.12 and the first slice of the repository
experience pack.

- One record type, `LessonItem`: `lesson`, `repository`, optional `version`, `failure` and `fix`, and
  `verified` (true only when the conversation shows the fix was checked).
- One view, `current`: what the writer sees before it writes and what recall offers.
- One writer, `io.agentmemory.examples.lessons:writer`, one chat call a turn after the core write. It sees
  the lessons already kept and what the core writer stored, updates a lesson under its own key, and names
  in `replaces` the lessons a new one makes wrong or obsolete. The host's closing approver (the core
  writer's own closing policy) decides each replacement.
- Host feature `Inference`: the host grants the chat model; the module never discovers one.

```csharp
services.AddAgentMemoryModules(modules => modules
    .UseNeo4jStore()
    .GrantChatClient(sp => sp.GetRequiredService<IChatClient>())
    .Add<RepositoryLessonsModule>());
```

Options bind from `AgentMemory:Modules:io.agentmemory.examples.lessons`: `Repository` (when set, the
repository of every lesson) and `MaxLessonsPerTurn` (1-20, default 5).

`RepositoryLessons.Checks` proves the packaged contracts with a scripted chat client and no database:
`python eng/verify-commitments-package.py --output artifacts/<new-folder>` runs it beside Commitments.
