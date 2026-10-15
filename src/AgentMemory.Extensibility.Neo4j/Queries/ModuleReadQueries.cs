using System.Diagnostics.CodeAnalysis;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Reading;

namespace AgentMemory.Extensibility.Neo4j.Queries;

[Experimental("AMEXT001")]
internal static class ModuleReadQueries
{
    internal static string Records(ModuleQuery query, ModuleType type)
    {
        var clauses = query.Where.Select((filter, index) => Filter(filter, type.Properties[filter.Property].Type, index));
        var order = query.OrderBy is { } sort ? "n.`" + sort.Property + "` IS NULL ASC, n.`" + sort.Property + "` " + (sort.Descending ? "DESC, " : "ASC, ") : "";
        var temporal = query.Selection == ModuleRecordSelection.History
            ? "($asOf IS NULL OR n.created_at<=datetime($asOf)) "
            : "(($asOf IS NULL AND n.invalidated_at IS NULL) OR ($asOf IS NOT NULL AND n.created_at<=datetime($asOf) AND (n.invalidated_at IS NULL OR n.invalidated_at>datetime($asOf)))) ";
        if (query.Selection == ModuleRecordSelection.History)
            order = "n.created_at DESC, toInteger(last(split(n.id, ':'))) DESC, ";
        return "MATCH (n:`" + type.Name + "`) WHERE ($owner IS NULL OR n.owner_id=$owner OR ($shared AND n.owner_id IS NULL)) "
            + "AND ($key IS NULL OR n.key=$key) AND "
            + temporal
            + string.Concat(clauses.Select(c => "AND (" + c + ") "))
            + "RETURN properties(n) AS row ORDER BY " + order + "n.id ASC SKIP $skip LIMIT $take";
    }

    private static string Filter(ModuleViewFilter filter, string type, int index)
    {
        var left = "n.`" + filter.Property + "`"; var parameter = "$f" + index;
        if (filter.Value.ValueKind == System.Text.Json.JsonValueKind.Null) return left + (filter.Operator == "=" ? " IS NULL" : " IS NOT NULL");
        if (filter.Operator == "in") return left + " IN [v IN " + parameter + " | " + Convert("v", type) + "]";
        return left + " " + (filter.Operator == "!=" ? "<>" : filter.Operator) + " " + Convert(parameter, type);
    }
    private static string Convert(string value, string type) => type switch
    { "date" => "date(" + value + ")", "datetime" => "datetime(" + value + ")", _ => value };

    internal static string Target(string label) => "MATCH (n:`" + label + "` {id:$target}) "
        + "WHERE (n.owner_id=$owner OR ($shared AND n.owner_id IS NULL)) "
        + "RETURN properties(n) AS row, "
        + "($asOf IS NOT NULL AND n.created_at<=datetime($asOf) AND (n.invalidated_at IS NULL OR n.invalidated_at>datetime($asOf))) AS historicalLive, "
        + "($asOf IS NOT NULL AND (n.created_at IS NULL OR n.created_at>datetime($asOf))) AS unborn LIMIT 2";
}
