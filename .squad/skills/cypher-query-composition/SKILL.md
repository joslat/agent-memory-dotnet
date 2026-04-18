# Skill: Cypher Query Composition

**Source:** HotChocolate.Data.Neo4J analysis (ChilliCream/graphql-platform)  
**Author:** Deckard  
**Date:** July 2026

## Pattern: Composable Query Builder with With* Methods

When building Cypher queries that have optional/dynamic clauses (WHERE conditions, ORDER BY, SKIP/LIMIT, vector search), use a composable builder pattern instead of string concatenation.

### Anti-Pattern (String Concatenation)
```csharp
var cypher = "MATCH (e:Entity)";
if (type != null) cypher += " WHERE e.type = $type";
if (minConfidence.HasValue)
    cypher += (type != null ? " AND" : " WHERE") + " e.confidence >= $min";
cypher += " RETURN e";
```

### Pattern (Composable Builder)
```csharp
var query = CypherBuilder.Match("(e:Entity)")
    .Where("e.type = $type", when: type != null)
    .Where("e.confidence >= $min", when: minConfidence.HasValue)
    .Return("e")
    .OrderBy("e.confidence DESC")
    .Skip(offset)
    .Limit(pageSize)
    .Build();  // Returns (string cypher, Dictionary<string, object?> parameters)
```

### N+1 Pagination Pattern
Request one extra item to detect `hasNextPage` without a separate COUNT query:
```csharp
var limit = request.PageSize + 1;
var items = await RunQuery(limit, offset);
var hasNext = items.Count > request.PageSize;
if (hasNext) items.RemoveAt(items.Count - 1);
return new PagedResult(items, hasNext, offset > 0);
```

### CompoundCondition for Filter Trees
For APIs that accept complex filter combinations:
```csharp
var filter = MemoryFilter.And(
    MemoryFilter.TypeEquals("Person"),
    MemoryFilter.Or(
        MemoryFilter.HasTag("important"),
        MemoryFilter.ModifiedAfter(cutoff)
    )
);
```

## When to Use
- Dynamic queries with optional WHERE clauses (>2 optional conditions)
- Paginated listing endpoints
- Context Assembler recall with multi-criteria filters

## When NOT to Use
- Fixed, known queries (use const string)
- Simple CRUD operations
- Queries with 0-1 optional conditions
