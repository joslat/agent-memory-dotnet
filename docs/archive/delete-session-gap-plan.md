# DELETE_SESSION_DATA Gap — Implementation Plan

**Branch:** `loop/delete-session-gap`  
**Author:** Deckard (Solution Architect)  
**Date:** 2026-04-30  
**Implementer:** Roy (Backend Dev)

---

## 1. Summary

Python's `DELETE_SESSION_DATA` operation atomically removes all three node types that belong to a session: `Message`, `Conversation`, and `ReasoningTrace` (plus child `ReasoningStep` nodes). The .NET `ClearSessionAsync` in `ShortTermMemoryService` only deletes Messages and Conversations, and the Conversation delete is an N+1 loop rather than a single batch Cypher. `ReasoningTrace` and `ReasoningStep` nodes are silently left behind, creating an observable data leak that breaks parity with the Python reference implementation.

This plan closes both issues with four small, additive changes: a new Cypher constant and repository method for each of the two missing operations, plus a two-line rewrite of `ClearSessionAsync` to call them.

---

## 2. Scope

### Changed
| Layer | File | Change |
|---|---|---|
| Abstractions | `IConversationRepository` | Add `DeleteBySessionAsync` |
| Abstractions | `IReasoningTraceRepository` | Add `DeleteBySessionAsync` |
| Neo4j Queries | `ConversationQueries` | Add `DeleteBySession` const |
| Neo4j Queries | `ReasoningQueries` | Add `DeleteBySession` const |
| Neo4j Repos | `Neo4jConversationRepository` | Implement `DeleteBySessionAsync` |
| Neo4j Repos | `Neo4jReasoningTraceRepository` | Implement `DeleteBySessionAsync` |
| Core Services | `ShortTermMemoryService` | Inject `IReasoningTraceRepository`; call both new methods in `ClearSessionAsync` |
| Unit Tests | `ShortTermMemoryServiceTests` | Update `ClearSessionAsync` test; remove N+1 assertions |
| Unit Tests | `Neo4jConversationRepositoryTitleTests` (or new file) | Add `DeleteBySessionAsync` Cypher test |
| Unit Tests | `Neo4jReasoningTraceRepositoryTests` | Add `DeleteBySessionAsync` Cypher test |
| Snapshot | `CypherQuerySnapshot.snap` + `CypherQuerySnapshotTests.cs` | Regenerate; bump `ExpectedQueryCount` 137 → 139 |

### Not Changed
- `IShortTermMemoryService` public interface signature — `ClearSessionAsync(string, CancellationToken)` is unchanged.
- `IMessageRepository` — `DeleteBySessionAsync` already exists and is called correctly.
- Integration tests — not in scope for this fix; no Neo4j container available in CI unit test run.
- Any other service, domain model, or DI registration beyond injecting `IReasoningTraceRepository` into `ShortTermMemoryService`.

---

## 3. Implementation Steps

### Step 1 — `ConversationQueries.cs`
**File:** `src/AgentMemory.Neo4j/Queries/ConversationQueries.cs`

Add a new `const` after the existing `Delete` constant:

```csharp
/// <summary>Delete all Conversations for a session (batch, replaces N+1).</summary>
public const string DeleteBySession = "MATCH (c:Conversation {session_id: $sessionId}) DETACH DELETE c";
```

### Step 2 — `IConversationRepository.cs`
**File:** `src/AgentMemory.Abstractions/Repositories/IConversationRepository.cs`

Add a new method below `DeleteAsync`:

```csharp
/// <summary>
/// Deletes all conversations for a session in a single batch operation.
/// </summary>
Task DeleteBySessionAsync(string sessionId, CancellationToken cancellationToken = default);
```

### Step 3 — `Neo4jConversationRepository.cs`
**File:** `src/AgentMemory.Neo4j/Repositories/Neo4jConversationRepository.cs`

Add the implementation after `DeleteAsync`:

```csharp
public async Task DeleteBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
{
    _logger.LogDebug("Deleting all conversations for session {SessionId}", sessionId);

    await _tx.WriteAsync(async runner =>
    {
        await runner.RunAsync(ConversationQueries.DeleteBySession, new { sessionId });
    }, cancellationToken);
}
```

### Step 4 — `ReasoningQueries.cs`
**File:** `src/AgentMemory.Neo4j/Queries/ReasoningQueries.cs`

Add a new `const` in the `// ── ReasoningTrace ──` section (e.g., after `ListTracesBySession`):

```csharp
/// <summary>
/// Delete all ReasoningTrace nodes for a session, including their child ReasoningStep nodes.
/// </summary>
public const string DeleteBySession = @"
        MATCH (t:ReasoningTrace {session_id: $sessionId})
        OPTIONAL MATCH (t)-[:HAS_STEP]->(s:ReasoningStep)
        DETACH DELETE t, s";
```

### Step 5 — `IReasoningTraceRepository.cs`
**File:** `src/AgentMemory.Abstractions/Repositories/IReasoningTraceRepository.cs`

Add a new method at the end of the interface:

```csharp
/// <summary>
/// Deletes all ReasoningTrace and child ReasoningStep nodes for a session.
/// </summary>
Task DeleteBySessionAsync(string sessionId, CancellationToken cancellationToken = default);
```

### Step 6 — `Neo4jReasoningTraceRepository.cs`
**File:** `src/AgentMemory.Neo4j/Repositories/Neo4jReasoningTraceRepository.cs`

Add the implementation at the end of the class (before the private helpers):

```csharp
public async Task DeleteBySessionAsync(string sessionId, CancellationToken cancellationToken = default)
{
    _logger.LogDebug("Deleting reasoning traces for session {SessionId}", sessionId);

    await _tx.WriteAsync(async runner =>
    {
        await runner.RunAsync(ReasoningQueries.DeleteBySession, new { sessionId });
    }, cancellationToken);
}
```

### Step 7 — `ShortTermMemoryService.cs`
**File:** `src/AgentMemory.Core/Services/ShortTermMemoryService.cs`

**a)** Add `IReasoningTraceRepository` field and constructor parameter alongside the existing repositories:

```csharp
private readonly IReasoningTraceRepository _reasoningTraceRepo;

// In constructor signature, add:
IReasoningTraceRepository reasoningTraceRepo,

// In constructor body, add:
_reasoningTraceRepo = reasoningTraceRepo;
```

**b)** Replace the entire `ClearSessionAsync` body:

```csharp
public async Task ClearSessionAsync(
    string sessionId,
    CancellationToken cancellationToken = default)
{
    _logger.LogDebug("Clearing session {SessionId}", sessionId);
    await _messageRepo.DeleteBySessionAsync(sessionId, cancellationToken);
    await _conversationRepo.DeleteBySessionAsync(sessionId, cancellationToken);
    await _reasoningTraceRepo.DeleteBySessionAsync(sessionId, cancellationToken);
}
```

The old `GetBySessionAsync` + `foreach DeleteAsync` loop is removed entirely.

---

## 4. Cypher Queries

### Conversation batch delete (replaces N+1)
```cypher
// Deletes all Conversation nodes for a session and detaches all their relationships.
MATCH (c:Conversation {session_id: $sessionId}) DETACH DELETE c
```
Parameter: `sessionId` (string)

### ReasoningTrace + ReasoningStep delete
```cypher
// Deletes all ReasoningTrace nodes for a session.
// OPTIONAL MATCH ensures the query succeeds even for traces with no steps.
// DETACH DELETE removes both node types and all their relationships atomically.
MATCH (t:ReasoningTrace {session_id: $sessionId})
OPTIONAL MATCH (t)-[:HAS_STEP]->(s:ReasoningStep)
DETACH DELETE t, s
```
Parameter: `sessionId` (string)

---

## 5. Testing

### 5.1 Unit test — `Neo4jConversationRepositoryTitleTests.cs` (or a new `Neo4jConversationRepositoryDeleteTests.cs`)
Add two tests following the existing write-capture pattern used throughout the repository test suite:

```
DeleteBySessionAsync_SendsCorrectCypher
  → Verify calls[0].Cypher contains "DETACH DELETE c" and session_id match.

DeleteBySessionAsync_PassesCorrectSessionId
  → Verify calls[0].Parameters has sessionId == "session-42".
```

### 5.2 Unit test — `Neo4jReasoningTraceRepositoryTests.cs`
Add two tests mirroring the existing `CreateInitiatedByRelationshipAsync` pattern:

```
DeleteBySessionAsync_SendsCorrectCypher
  → Verify calls[0].Cypher contains "DETACH DELETE t, s".

DeleteBySessionAsync_PassesCorrectSessionId
  → Verify calls[0].Parameters has sessionId == "session-42".
```

### 5.3 Unit test — `ShortTermMemoryServiceTests.cs`
Update the existing `ClearSessionAsync_DeletesMessagesAndConversations` test:

- Rename to `ClearSessionAsync_DeletesMessages_Conversations_AndReasoningTraces` (or add a new test and leave the old one to confirm backward compat).
- Inject a mock `IReasoningTraceRepository` in the test constructor and `CreateSut()` factory.
- Set up `_reasoningTraceRepo.DeleteBySessionAsync(...)` to return `Task.CompletedTask`.
- Assert: `_messageRepo.Received(1).DeleteBySessionAsync("session-1", ...)`.
- Assert: `_conversationRepo.Received(1).DeleteBySessionAsync("session-1", ...)`.
- Assert: `_reasoningTraceRepo.Received(1).DeleteBySessionAsync("session-1", ...)`.
- Assert: `_conversationRepo.DidNotReceive().GetBySessionAsync(...)` — confirms N+1 is gone.
- Assert: `_conversationRepo.DidNotReceive().DeleteAsync(...)` — confirms old single-delete is gone.

### 5.4 Snapshot — `CypherQuerySnapshotTests.cs` + `CypherQuerySnapshot.snap`

Adding two new `const` strings increments the catalog by 2. After the code changes:

1. Update `ExpectedQueryCount` in `CypherQuerySnapshotTests.cs` from **137 to 139**.
2. Regenerate the snapshot:
   ```
   $env:UPDATE_CYPHER_SNAPSHOTS = "1"
   dotnet test --filter "CypherCatalog_MatchesSnapshot"
   ```
3. Commit the updated `.snap` file alongside the code changes.

---

## 6. Acceptance Criteria

- [ ] `dotnet build` succeeds with zero errors or warnings introduced by these changes.
- [ ] All existing unit tests pass without modification (except the renamed/updated `ClearSessionAsync` test).
- [ ] New unit tests for `DeleteBySessionAsync` on both repositories pass.
- [ ] `CypherCatalog_MatchesSnapshot` passes with `ExpectedQueryCount = 139` and regenerated `.snap`.
- [ ] `ClearSessionAsync` no longer calls `GetBySessionAsync` or `DeleteAsync(conversationId)` — confirmed by `DidNotReceive` assertions.
- [ ] `ReasoningTrace` and `ReasoningStep` nodes are deleted when `ClearSessionAsync` is called — confirmed by the new `_reasoningTraceRepo.Received(1).DeleteBySessionAsync` assertion.
