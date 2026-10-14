using System.Diagnostics.CodeAnalysis;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;
using System.Text.RegularExpressions;
using AgentMemory.Extensibility.Modules;
using AgentMemory.Shared;

namespace AgentMemory.Extensibility.Neo4j.Migrations;

/// <summary>Reads migration definitions as data; discovery is not permission to execute them.</summary>
/// <remarks>Embedded Migrations/000N_name.cypher resources must be UTF-8 without a BOM, at most
/// 256 KiB each, 1 MiB combined and 128 scripts per assembly. Default dotted MSBuild resource names
/// and root Migrations/ logical names are accepted. Namespace, operation and database ownership
/// checks are separate obligations; these definitions cannot yet be supplied to ModuleMigrator.</remarks>
[Experimental("AMEXT001")]
public static class ModuleMigrationScripts
{
    private const int MaximumScriptBytes = 256 * 1024;
    private const int MaximumTotalBytes = 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly Regex FileName = new(@"\A[0-9]{4}_[A-Za-z][A-Za-z0-9_]*\.cypher\z", RegexOptions.CultureInvariant);

    /// <summary>Reads embedded sources and validates the module's manifest/version without loading code.</summary>
    /// <remarks>Hashes cover the complete source, including comments, with only line endings normalized.</remarks>
    public static IReadOnlyList<ModuleMigrationStep> LoadFromAssemblyImage(ReadOnlyMemory<byte> image)
    {
        // The portable reader owns image bounds, manifest validation and informational-version agreement.
        if (image.Length > ModuleManifest.MaximumAssemblyBytes) throw Invalid("assembly image exceeds 64 MiB");
        var snapshot = image.ToArray();
        var manifest = ModuleManifest.LoadFromAssemblyImage(snapshot);
        try
        {
            using var stream = new MemoryStream(snapshot, writable: false);
            using var pe = new PEReader(stream);
            var metadata = pe.GetMetadataReader();
            var steps = new List<ModuleMigrationStep>();
            var keys = new HashSet<string>(StringComparer.Ordinal);
            var totalBytes = 0;
            foreach (var handle in metadata.ManifestResources)
            {
                var resource = metadata.GetManifestResource(handle);
                var name = ResourceFileName(metadata.GetString(resource.Name));
                if (name is null) continue;
                if (!FileName.IsMatch(name)) throw Invalid("expected Migrations/000N_name.cypher: " + name);
                if (steps.Count == 128) throw Invalid("more than 128 embedded scripts");
                var key = name[..^".cypher".Length];
                if (!keys.Add(key)) throw Invalid("duplicate migration key: " + key);
                var bytes = ManagedEmbeddedResources.Read(pe, resource, MaximumScriptBytes, "Module migration " + key);
                totalBytes += bytes.Length;
                if (totalBytes > MaximumTotalBytes) throw Invalid("combined embedded scripts exceed 1 MiB");
                var source = StrictUtf8.GetString(bytes);
                if (source.StartsWith('\uFEFF')) throw Invalid("migration UTF-8 must not contain a BOM: " + key);
                if (ModuleCypherScript.Parse(source).Count == 0) throw Invalid("empty migration: " + key);
                steps.Add(new ModuleMigrationStep
                {
                    ModuleId = manifest.Id, Key = key, Statement = source,
                    Sha256 = ModuleMigrationDefinitions.Hash(source)
                });
            }
            return steps.OrderBy(step => step.Key, StringComparer.Ordinal).ToList().AsReadOnly();
        }
        catch (DecoderFallbackException e) { throw new FormatException("Module migration: script is not valid UTF-8.", e); }
        catch (BadImageFormatException e) { throw new FormatException("Module migration: malformed managed assembly image.", e); }
        catch (IOException e) { throw new FormatException("Module migration: truncated managed assembly image.", e); }
        catch (OverflowException e) { throw new FormatException("Module migration: resource metadata exceeds supported bounds.", e); }
    }

    private static string? ResourceFileName(string resourceName)
    {
        const string dotted = ".Migrations.";
        if (resourceName.StartsWith("Migrations/", StringComparison.Ordinal)) return resourceName["Migrations/".Length..];
        if (resourceName.StartsWith("Migrations.", StringComparison.Ordinal)) return resourceName["Migrations.".Length..];
        var marker = resourceName.IndexOf(dotted, StringComparison.Ordinal);
        return marker < 0 ? null : resourceName[(marker + dotted.Length)..];
    }

    private static FormatException Invalid(string message) => new("Module migration: " + message + ".");
}
