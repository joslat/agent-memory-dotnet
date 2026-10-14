using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Extensibility.Neo4j.Queries;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

[Experimental("AMEXT001")]
internal static class ModuleMigrationDefinitions
{
    internal static IReadOnlyList<ModuleMigrationStep> Derive(ModuleResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        if (!resolution.Succeeded)
            throw new ArgumentException("Module migration definitions require successful complete-set resolution.", nameof(resolution));

        var steps = new List<ModuleMigrationStep>();
        foreach (var module in resolution.Modules)
        {
            foreach (var shape in ModuleSchemaObjects.Describe(module))
            {
                var statement = ModuleSchemaQueries.Create(shape);
                steps.Add(new ModuleMigrationStep
                {
                    ModuleId = module.Id,
                    Key = "derived/" + shape.Name,
                    Statement = statement,
                    Sha256 = Hash(statement)
                });
            }
        }
        return steps.AsReadOnly();
    }

    internal static string Hash(string statement)
    {
        ArgumentNullException.ThrowIfNull(statement);
        var normalized = statement.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("\r", "\n", StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
