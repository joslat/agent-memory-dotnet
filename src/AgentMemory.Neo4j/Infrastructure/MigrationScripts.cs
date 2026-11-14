using System.Reflection;

namespace AgentMemory.Neo4j.Infrastructure;

/// <summary>
/// Where the migration runner's Cypher scripts come from. Names are relative and use '/': <c>000N_name.cypher</c> for the
/// base sequence, <c>ext/&lt;id&gt;/000N_name.cypher</c> for a schema extension's.
/// </summary>
internal abstract class MigrationScripts
{
    /// <summary>Where the scripts were looked for, for logs.</summary>
    public abstract string Location { get; }

    /// <summary>Every script name, base and extensions, in ordinal order.</summary>
    public abstract IReadOnlyList<string> Names();

    public abstract Task<string> ReadAsync(string name, CancellationToken cancellationToken);

    /// <summary>The base sequence's script names (no folder), in ordinal order.</summary>
    public IReadOnlyList<string> Base() => [.. Names().Where(name => !name.Contains('/', StringComparison.Ordinal))];

    /// <summary>One extension's script names (with their <c>ext/&lt;id&gt;/</c> prefix), in ordinal order.</summary>
    public IReadOnlyList<string> Extension(string id)
    {
        var prefix = $"{MigrationRunner.ExtensionFolder}/{id}/";
        return [.. Names().Where(name => name.StartsWith(prefix, StringComparison.Ordinal) &&
                                         !name[prefix.Length..].Contains('/', StringComparison.Ordinal))];
    }
}

/// <summary>
/// The scripts embedded in AgentMemory.Neo4j at build time: what every installed application runs, however the package
/// reached it. Copying files next to the application did not reach consumers of the meta-package, because NuGet does not
/// pass a dependency's content files on.
/// </summary>
internal sealed class EmbeddedMigrationScripts : MigrationScripts
{
    /// <summary>The logical-name prefix the project file gives every embedded script.</summary>
    internal const string ResourcePrefix = "AgentMemory.Neo4j.Migrations/";

    public static readonly EmbeddedMigrationScripts Shipped = new(typeof(EmbeddedMigrationScripts).Assembly);

    private readonly Assembly _assembly;
    private readonly IReadOnlyDictionary<string, string> _resources;

    internal EmbeddedMigrationScripts(Assembly assembly)
    {
        _assembly = assembly;
        // RecursiveDir gives '\' on Windows builds and '/' elsewhere: one spelling, whichever built the package.
        _resources = assembly.GetManifestResourceNames()
            .Where(resource => resource.StartsWith(ResourcePrefix, StringComparison.Ordinal) &&
                               resource.EndsWith(".cypher", StringComparison.Ordinal))
            .ToDictionary(resource => resource[ResourcePrefix.Length..].Replace('\\', '/'), resource => resource, StringComparer.Ordinal);
    }

    public override string Location => $"the {_assembly.GetName().Name} assembly";

    public override IReadOnlyList<string> Names() => [.. _resources.Keys.Order(StringComparer.Ordinal)];

    public override async Task<string> ReadAsync(string name, CancellationToken cancellationToken)
    {
        var stream = _assembly.GetManifestResourceStream(_resources[name])
            ?? throw new InvalidOperationException($"Embedded migration {name} could not be opened.");
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>Scripts in a folder: the test seam, so a test can run its own scripts or none.</summary>
internal sealed class DirectoryMigrationScripts(string directory) : MigrationScripts
{
    public override string Location => directory;

    public override IReadOnlyList<string> Names() =>
        !Directory.Exists(directory) ? [] :
        [.. Directory.GetFiles(directory, "*.cypher", SearchOption.AllDirectories)
            .Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
            .Order(StringComparer.Ordinal)];

    public override Task<string> ReadAsync(string name, CancellationToken cancellationToken) =>
        File.ReadAllTextAsync(Path.Combine(directory, name), cancellationToken);
}
