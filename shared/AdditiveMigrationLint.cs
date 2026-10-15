using System.Text.RegularExpressions;

namespace AgentMemory.Shared;

internal static class AdditiveMigrationLint
{
    private static readonly Regex AllowedStatement = new(
        @"^\s*(CREATE\s+(INDEX|CONSTRAINT|VECTOR\s+INDEX|FULLTEXT\s+INDEX)\b.*IF\s+NOT\s+EXISTS\b|MATCH\b)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static readonly Regex ForbiddenStatement = new(
        @"\b(DROP|REMOVE|DETACH\s+DELETE|DELETE)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    internal static IReadOnlyList<string> Validate(IEnumerable<string> statements, string identity)
    {
        var problems = new List<string>();

        foreach (var statement in statements)
        {
            if (ForbiddenStatement.IsMatch(statement))
            {
                problems.Add(
                    $"{identity}: statement removes or rewrites existing shape "
                    + $"(R1 is additive-only): {Excerpt(statement)}");
                continue;
            }

            if (!AllowedStatement.IsMatch(statement))
            {
                problems.Add(
                    $"{identity}: statement is neither a 'CREATE … IF NOT EXISTS' "
                    + $"schema object nor a MATCH-scoped backfill: {Excerpt(statement)}");
            }
        }

        return problems;
    }

    private static string Excerpt(string statement) =>
        statement.Length <= 120 ? statement : statement[..120] + "…";
}
