using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using FluentAssertions;
using AgentMemory.Neo4j.Queries;
using AgentMemory.Tests.Integration.Fixtures;
using Neo4j.Driver;
using Xunit.Abstractions;

namespace AgentMemory.Tests.Integration.Queries;

/// <summary>
/// Live-Neo4j <b>query-execution sweep</b>. Reflects over every centralized Cypher query in
/// <c>AgentMemory.Neo4j.Queries</c> — both <c>public const string</c> fields and the flag/enum-built
/// <c>public static string</c> methods — and runs each through Neo4j's <c>EXPLAIN</c> against a real
/// container. <c>EXPLAIN</c> parses <em>and</em> plans a query (validating syntax AND that labels,
/// properties, functions and procedures resolve) <b>without executing it</b>, so no seed data is needed
/// and dummy parameters suffice.
/// <para>
/// This catches the class of bug that the string-only <c>CypherQuerySnapshotTests</c> cannot: a query
/// that is a valid C# string but invalid Cypher (e.g. the <c>collect(m ORDER BY ...)</c> and
/// <c>SHOW INDEXES WHERE ... RETURN</c> defects that shipped and were only caught at runtime). Any new
/// such regression fails this test with an actionable list of the offending queries and driver errors.
/// </para>
/// <para>
/// Queries that <c>EXPLAIN</c> genuinely cannot plan are skipped narrowly and reported: administrative
/// <c>SHOW</c> commands, schema DDL (<c>CREATE</c>/<c>DROP</c> <c>CONSTRAINT</c>/<c>INDEX</c>), and any
/// multi-statement text. A procedure that is legitimately absent (GDS/APOC) is reclassified as a skip,
/// not a failure — only a syntax/semantic error is a bug.
/// </para>
/// </summary>
[Collection("Neo4j Integration")]
[Trait("Category", "Integration")]
public sealed class CypherQueryExecutionSweepTests
{
    private const string QueriesNamespace = "AgentMemory.Neo4j.Queries";

    private readonly Neo4jIntegrationFixture _fixture;
    private readonly ITestOutputHelper _output;

    public CypherQueryExecutionSweepTests(Neo4jIntegrationFixture fixture, ITestOutputHelper output)
    {
        _fixture = fixture;
        _output = output;
    }

    [Fact]
    public async Task EveryCypherQuery_ParsesAndPlans_UnderExplain()
    {
        var collected = CollectQueries(out var uninvokableMethods);

        var failures = new List<string>();
        var skipped = new List<string>(uninvokableMethods);
        var swept = 0;

        foreach (var (origin, cypher) in collected)
        {
            if (ShouldSkipForExplain(cypher, out var skipReason))
            {
                skipped.Add($"{origin} — {skipReason}");
                continue;
            }

            var outcome = await TryExplainAsync(cypher);
            switch (outcome.Kind)
            {
                case ExplainResultKind.Planned:
                    swept++;
                    break;
                case ExplainResultKind.SkippedProcedureAbsent:
                    skipped.Add($"{origin} — procedure absent ({outcome.Detail})");
                    break;
                default:
                    failures.Add($"{origin}\n        {outcome.Detail}");
                    break;
            }
        }

        // Surface the coverage numbers on every run (visible with `--logger "console;verbosity=detailed"`).
        _output.WriteLine($"Cypher execution sweep: EXPLAIN-validated {swept} queries, skipped {skipped.Count}.");
        foreach (var s in skipped.OrderBy(x => x, StringComparer.Ordinal))
            _output.WriteLine("  SKIP  " + s);

        swept.Should().BeGreaterThan(0,
            "the sweep must actually EXPLAIN real queries — if everything was skipped the harness is broken");

        failures.Should().BeEmpty(
            "every centralized Cypher query must be valid, plannable Cypher against a live Neo4j. " +
            $"{failures.Count} query/queries failed EXPLAIN:\n    " +
            string.Join("\n    ", failures));
    }

    // ── Query discovery ─────────────────────────────────────────────────────────

    /// <summary>
    /// Reflects the <c>AgentMemory.Neo4j.Queries</c> namespace for (1) every <c>public const string</c>
    /// field and (2) every <c>public static</c> method returning <c>string</c> whose parameters are all
    /// values the sweep can supply (bool → both; int/long → a sample; enum → every value plus null when
    /// nullable; string → a valid label / null fragment / value-gate pair). Each method is invoked for
    /// the full cartesian product of those candidate arguments so both the scoped and unscoped shapes of
    /// a flag-built query are covered. Methods with an unsupported parameter (e.g. a <c>CypherBuilder</c>)
    /// are recorded in <paramref name="uninvokableMethods"/> rather than silently dropped.
    /// </summary>
    private static List<(string Origin, string Cypher)> CollectQueries(out List<string> uninvokableMethods)
    {
        uninvokableMethods = new List<string>();
        var result = new List<(string, string)>();

        var types = typeof(FactQueries).Assembly
            .GetTypes()
            .Where(t => t.Namespace == QueriesNamespace)
            .OrderBy(t => t.Name, StringComparer.Ordinal);

        foreach (var type in types)
        {
            // (1) public const string fields
            foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Static)
                                      .OrderBy(f => f.Name, StringComparer.Ordinal))
            {
                if (!field.IsLiteral || field.FieldType != typeof(string))
                    continue;

                var value = (string?)field.GetValue(null);
                if (!string.IsNullOrWhiteSpace(value))
                    result.Add(($"{type.Name}.{field.Name}", value));
            }

            // (2) public static string-returning methods with sweep-supplyable parameters
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static)
                                       .OrderBy(m => m.Name, StringComparer.Ordinal))
            {
                if (method.ReturnType != typeof(string) || method.IsSpecialName)
                    continue;

                var parameters = method.GetParameters();
                var perParamCandidates = new List<object?[]>();
                var supported = true;

                foreach (var p in parameters)
                {
                    if (TryGetArgumentCandidates(p, out var candidates))
                        perParamCandidates.Add(candidates);
                    else
                    {
                        supported = false;
                        break;
                    }
                }

                if (!supported)
                {
                    uninvokableMethods.Add(
                        $"{type.Name}.{method.Name} — unsupported parameter type(s), not invoked");
                    continue;
                }

                foreach (var args in CartesianProduct(perParamCandidates))
                {
                    string built;
                    try
                    {
                        built = (string)method.Invoke(null, args)!;
                    }
                    catch (TargetInvocationException)
                    {
                        // The method itself rejected this argument combination (e.g. an out-of-range enum);
                        // that combination is not a real query shape, so skip it silently.
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(built))
                        result.Add(($"{type.Name}.{method.Name}({FormatArgs(args)})", built));
                }
            }
        }

        return result;
    }

    /// <summary>Builds the candidate argument list for a single method parameter, or false if unsupported.</summary>
    private static bool TryGetArgumentCandidates(ParameterInfo p, out object?[] candidates)
    {
        var t = p.ParameterType;

        if (t == typeof(bool)) { candidates = new object?[] { false, true }; return true; }
        if (t == typeof(int)) { candidates = new object?[] { 5 }; return true; }
        if (t == typeof(long)) { candidates = new object?[] { 5L }; return true; }

        var enumType = Nullable.GetUnderlyingType(t) ?? t;
        if (enumType.IsEnum)
        {
            var values = Enum.GetValues(enumType).Cast<object?>().ToList();
            if (Nullable.GetUnderlyingType(t) != null)
                values.Insert(0, null); // nullable enum: also exercise the null branch
            candidates = values.ToArray();
            return true;
        }

        if (t == typeof(string))
        {
            var name = p.Name ?? string.Empty;
            if (name.Equals("label", StringComparison.OrdinalIgnoreCase))
                // Interpolated as a node label — must be a real label, never null.
                candidates = new object?[] { "Fact" };
            else if (name.Contains("fragment", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("clause", StringComparison.OrdinalIgnoreCase))
                // A raw pre-built Cypher fragment — only the null (no-fragment) shape is safe to synthesize.
                candidates = new object?[] { null };
            else
                // A value gate (e.g. entity `type`): null exercises the off branch, a value the on branch.
                candidates = new object?[] { null, "x" };
            return true;
        }

        candidates = Array.Empty<object?>();
        return false;
    }

    private static IEnumerable<object?[]> CartesianProduct(IReadOnlyList<object?[]> candidateLists)
    {
        if (candidateLists.Count == 0)
        {
            yield return Array.Empty<object?>();
            yield break;
        }

        var indices = new int[candidateLists.Count];
        while (true)
        {
            var combo = new object?[candidateLists.Count];
            for (var i = 0; i < candidateLists.Count; i++)
                combo[i] = candidateLists[i][indices[i]];
            yield return combo;

            var position = candidateLists.Count - 1;
            while (position >= 0)
            {
                indices[position]++;
                if (indices[position] < candidateLists[position].Length) break;
                indices[position] = 0;
                position--;
            }
            if (position < 0) yield break;
        }
    }

    private static string FormatArgs(object?[] args) =>
        string.Join(", ", args.Select(a => a switch
        {
            null => "null",
            bool b => b ? "true" : "false",
            string s => $"\"{s}\"",
            _ => Convert.ToString(a, CultureInfo.InvariantCulture) ?? a.ToString()
        }));

    // ── EXPLAIN eligibility ─────────────────────────────────────────────────────

    /// <summary>
    /// True when a query cannot be planned by <c>EXPLAIN</c> and must be skipped narrowly: administrative
    /// <c>SHOW</c> commands, schema DDL (<c>CREATE</c>/<c>DROP</c> <c>CONSTRAINT</c>/<c>INDEX</c>), or any
    /// multi-statement text. Ordinary reads and writes (MATCH/MERGE/CREATE-node/CALL/…) are NOT skipped.
    /// </summary>
    private static bool ShouldSkipForExplain(string cypher, out string reason)
    {
        var trimmed = cypher.TrimStart();

        if (trimmed.StartsWith("SHOW ", StringComparison.OrdinalIgnoreCase))
        {
            reason = "administrative SHOW command (EXPLAIN cannot plan it)";
            return true;
        }

        if (Regex.IsMatch(trimmed,
                @"^CREATE\s+(CONSTRAINT|(RANGE|TEXT|POINT|FULLTEXT|VECTOR|LOOKUP)\s+INDEX|INDEX)\b",
                RegexOptions.IgnoreCase))
        {
            reason = "schema DDL CREATE CONSTRAINT/INDEX (EXPLAIN cannot plan it)";
            return true;
        }

        if (trimmed.StartsWith("DROP ", StringComparison.OrdinalIgnoreCase))
        {
            reason = "schema DDL DROP (EXPLAIN cannot plan it)";
            return true;
        }

        if (cypher.Contains(';'))
        {
            reason = "multi-statement query (EXPLAIN plans a single statement)";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    // ── Adaptive EXPLAIN ─────────────────────────────────────────────────────────

    private enum ExplainResultKind { Planned, SkippedProcedureAbsent, Failed }

    private readonly record struct ExplainResult(ExplainResultKind Kind, string Detail);

    /// <summary>
    /// Runs <c>EXPLAIN</c> on a query with best-guess dummy parameters. Because Neo4j infers parameter
    /// types from usage, a wrong dummy type surfaces as a <c>Type mismatch for parameter 'x': expected T</c>
    /// error — which is an artifact of the dummy value, never of the query text. Such an error is parsed,
    /// the offending parameter is re-coerced to the exact expected type, and the plan is retried. Any error
    /// that is <b>not</b> a parameter type-mismatch (a genuine syntax/semantic defect) is returned as a
    /// failure; an absent GDS/APOC procedure is returned as a benign skip.
    /// </summary>
    private async Task<ExplainResult> TryExplainAsync(string cypher)
    {
        var parameters = BuildDummyParameters(cypher);
        var coercions = new HashSet<string>(StringComparer.Ordinal); // guards against an oscillating parameter
        const int maxAttempts = 32;

        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            // A fresh session per attempt so a failed auto-commit transaction can never poison the next probe.
            await using var session = _fixture.Driver.AsyncSession();
            try
            {
                var cursor = await session.RunAsync("EXPLAIN " + cypher, parameters);
                await cursor.ConsumeAsync();
                return new ExplainResult(ExplainResultKind.Planned, string.Empty);
            }
            catch (Neo4jException ex) when (IsBenignProcedureAbsence(ex))
            {
                // GDS/APOC (or any unregistered procedure) missing from the base image is not a query bug.
                return new ExplainResult(ExplainResultKind.SkippedProcedureAbsent, ex.Code ?? ex.Message);
            }
            catch (Neo4jException ex)
            {
                // A dummy-value type mismatch is a harness artifact: re-coerce that parameter and retry.
                if (TryReadParameterTypeMismatch(ex.Message, out var paramName, out var expectedType)
                    && CoerceForExpectedType(expectedType) is { } corrected
                    && parameters.ContainsKey(paramName)
                    && coercions.Add($"{paramName}=>{expectedType}")) // stop if we would repeat a coercion
                {
                    parameters[paramName] = corrected;
                    continue;
                }

                return new ExplainResult(ExplainResultKind.Failed, $"[{ex.Code}] {FirstLine(ex.Message)}");
            }
        }

        return new ExplainResult(ExplainResultKind.Failed,
            "could not satisfy parameter types within the retry budget (possible conflicting parameter usage)");
    }

    /// <summary>
    /// A missing procedure (e.g. GDS/APOC absent from the base image) is not a query bug. A missing
    /// <b>index</b> is deliberately NOT treated as benign here — index names are runtime string arguments
    /// that EXPLAIN does not resolve, so a genuine index error would indicate something worth surfacing.
    /// </summary>
    private static bool IsBenignProcedureAbsence(Neo4jException ex)
    {
        var code = ex.Code ?? string.Empty;
        if (code.Contains("ProcedureNotFound", StringComparison.OrdinalIgnoreCase))
            return true;

        var message = ex.Message ?? string.Empty;
        return message.Contains("no procedure with the name", StringComparison.OrdinalIgnoreCase)
            || message.Contains("There is no procedure", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryReadParameterTypeMismatch(string message, out string paramName, out string expectedType)
    {
        var m = Regex.Match(message,
            @"Type mismatch for parameter '(?<p>[^']+)': expected (?<t>.+?) but was");
        if (m.Success)
        {
            paramName = m.Groups["p"].Value;
            expectedType = m.Groups["t"].Value.Trim();
            return true;
        }

        paramName = string.Empty;
        expectedType = string.Empty;
        return false;
    }

    /// <summary>Maps a Neo4j-reported expected parameter type to a value of that type, or null if unknown.</summary>
    private static object? CoerceForExpectedType(string expected)
    {
        if (expected.Contains("Boolean", StringComparison.OrdinalIgnoreCase))
            return true;

        if (expected.Contains("List", StringComparison.OrdinalIgnoreCase))
            return expected.Contains("Float", StringComparison.OrdinalIgnoreCase)
                || expected.Contains("Number", StringComparison.OrdinalIgnoreCase)
                ? new[] { 0.1, 0.2, 0.3, 0.4 }
                : new List<object>();

        // "Integer" alone must stay an integer (e.g. SKIP/LIMIT/queryNodes count); anything mentioning
        // Float (including "Float or Integer") is satisfied by a double.
        if (expected.Equals("Integer", StringComparison.OrdinalIgnoreCase))
            return 10L;
        if (expected.Contains("Float", StringComparison.OrdinalIgnoreCase))
            return 1.0d;
        if (expected.Contains("Integer", StringComparison.OrdinalIgnoreCase))
            return 10L;

        if (expected.Contains("String", StringComparison.OrdinalIgnoreCase))
            return "x";
        if (expected.Contains("Map", StringComparison.OrdinalIgnoreCase))
            return new Dictionary<string, object>();
        if (expected.Contains("Date", StringComparison.OrdinalIgnoreCase)
            || expected.Contains("Time", StringComparison.OrdinalIgnoreCase)
            || expected.Contains("Duration", StringComparison.OrdinalIgnoreCase))
            return "2020-01-01T00:00:00Z";

        return null;
    }

    // ── Dummy parameters ────────────────────────────────────────────────────────

    /// <summary>
    /// Extracts every distinct <c>$param</c> token from a query and supplies a best-guess dummy value that
    /// keeps the Cypher planner's type inference happy on the first attempt: an integer for <c>SKIP</c>,
    /// <c>LIMIT</c> and the <c>queryNodes</c> neighbour count; a list for <c>UNWIND</c> and <c>IN</c>
    /// targets; an ISO-8601 string for tokens wrapped in <c>datetime()</c>/<c>date()</c>; a float vector
    /// for embedding tokens; a float for multiplicative-arithmetic tokens; and a plain string otherwise.
    /// Anything guessed wrong is corrected from EXPLAIN's own error via <see cref="TryExplainAsync"/>.
    /// EXPLAIN does not execute the query, so these values are never evaluated — they only need to plan.
    /// </summary>
    private static Dictionary<string, object> BuildDummyParameters(string cypher)
    {
        var parameters = new Dictionary<string, object>(StringComparer.Ordinal);

        foreach (Match m in Regex.Matches(cypher, @"\$(\w+)"))
        {
            var name = m.Groups[1].Value;
            if (parameters.ContainsKey(name)) continue;
            parameters[name] = DummyValueFor(name, cypher);
        }

        return parameters;
    }

    private static object DummyValueFor(string name, string cypher)
    {
        var token = Regex.Escape(name);

        // SKIP/LIMIT expressions and the queryNodes neighbour count must be integer-typed for the planner.
        if (Regex.IsMatch(cypher, $@"\b(SKIP|LIMIT)\s+\${token}\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(cypher, $@"queryNodes\(\s*'[^']*'\s*,\s*\${token}\b", RegexOptions.IgnoreCase))
            return 10L;

        // UNWIND and IN targets must be lists.
        if (Regex.IsMatch(cypher, $@"\bUNWIND\s+\${token}\b", RegexOptions.IgnoreCase)
            || Regex.IsMatch(cypher, $@"\bIN\s+\${token}\b", RegexOptions.IgnoreCase))
            return new List<object>();

        // Temporal constructors want a parseable timestamp shape.
        if (Regex.IsMatch(cypher, $@"\b(datetime|localdatetime|date|time)\s*\(\s*\${token}\b", RegexOptions.IgnoreCase))
            return "2020-01-01T00:00:00Z";

        // Vector-search query vectors.
        if (name.Contains("embedding", StringComparison.OrdinalIgnoreCase))
            return new[] { 0.1, 0.2, 0.3, 0.4 };

        // Multiplicative arithmetic (e.g. exp(-$lambda * daysSince), $boostFactor * ac) forces a numeric.
        if (Regex.IsMatch(cypher, $@"\${token}\s*[*/]") || Regex.IsMatch(cypher, $@"[*/]\s*\${token}\b"))
            return 1.0d;

        return "x";
    }

    private static string FirstLine(string message)
    {
        var idx = message.IndexOfAny(new[] { '\r', '\n' });
        return idx < 0 ? message : message[..idx];
    }
}
