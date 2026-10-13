using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace AgentMemory.Extensibility.Modules;

[Experimental("AMEXT001")]
internal static class ModuleAssemblyReader
{
    private const string Suffix = ".agentmemory-module.json";
    private const string VersionAttributeName = "System.Reflection.AssemblyInformationalVersionAttribute";
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    internal static ModuleManifest ReadFile(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > ModuleManifest.MaximumAssemblyBytes) throw Invalid("assembly image exceeds 64 MiB");
        return ReadImage(ReadBounded(stream, ModuleManifest.MaximumAssemblyBytes));
    }

    internal static ModuleManifest ReadImage(ReadOnlyMemory<byte> image)
    {
        if (image.Length > ModuleManifest.MaximumAssemblyBytes) throw Invalid("assembly image exceeds 64 MiB");
        try
        {
            using var stream = new MemoryStream(image.ToArray(), writable: false);
            using var pe = new PEReader(stream);
            if (!pe.HasMetadata || pe.PEHeaders.CorHeader is null) throw Invalid("expected managed assembly metadata");
            var metadata = pe.GetMetadataReader();
            if (!metadata.IsAssembly) throw Invalid("a netmodule is not a module implementation assembly");
            var resources = metadata.ManifestResources.Select(metadata.GetManifestResource)
                .Where(r => metadata.GetString(r.Name).EndsWith(Suffix, StringComparison.Ordinal)).ToArray();
            if (resources.Length != 1) throw Invalid("expected exactly one embedded module manifest");
            var resource = resources[0];
            if (!resource.Implementation.IsNil) throw Invalid("module manifest must be embedded, not linked externally");
            var directory = pe.PEHeaders.CorHeader.ResourcesDirectory;
            if (directory.Size < 4 || resource.Offset > directory.Size - 4)
                throw Invalid("manifest resource offset is outside the declared resource directory");
            var section = pe.GetSectionData(directory.RelativeVirtualAddress);
            if (directory.Size > section.Length) throw Invalid("truncated managed resource directory");
            var bytes = section.GetReader(checked((int)resource.Offset), directory.Size - checked((int)resource.Offset));
            var length = bytes.ReadUInt32();
            if (length > ModuleManifest.MaximumUtf8Bytes || length > bytes.RemainingBytes)
                throw Invalid("manifest resource length is out of bounds");
            var manifest = Parse(bytes.ReadBytes((int)length));
            CheckVersion(manifest, InformationalVersion(metadata));
            return manifest;
        }
        catch (BadImageFormatException e) { throw new FormatException("Module discovery: malformed managed assembly image.", e); }
        catch (IOException e) { throw new FormatException("Module discovery: truncated managed assembly image.", e); }
        catch (OverflowException e) { throw new FormatException("Module discovery: resource metadata exceeds supported bounds.", e); }
    }

    internal static ModuleManifest ReadLoaded(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        var names = assembly.GetManifestResourceNames().Where(n => n.EndsWith(Suffix, StringComparison.Ordinal)).ToArray();
        if (names.Length != 1) throw Invalid("expected exactly one embedded module manifest");
        var info = assembly.GetManifestResourceInfo(names[0]);
        if (info is null || !info.ResourceLocation.HasFlag(ResourceLocation.Embedded))
            throw Invalid("module manifest must be embedded, not linked externally");
        using var resource = assembly.GetManifestResourceStream(names[0]) ?? throw Invalid("embedded manifest unavailable");
        var manifest = Parse(ReadBounded(resource, ModuleManifest.MaximumUtf8Bytes));
        // CustomAttributeData reads metadata. GetCustomAttributes would execute arbitrary constructors.
        var attributes = assembly.GetCustomAttributesData()
            .Where(a => a.AttributeType.FullName == VersionAttributeName).ToArray();
        if (attributes.Length != 1 || attributes[0].AttributeType != typeof(AssemblyInformationalVersionAttribute)
            || attributes[0].ConstructorArguments.Count != 1 || attributes[0].NamedArguments.Count != 0)
            throw Invalid("expected exactly one standard informational version attribute");
        CheckVersion(manifest, attributes[0].ConstructorArguments[0].Value as string);
        return manifest;
    }

    private static string? InformationalVersion(MetadataReader metadata)
    {
        string? result = null;
        var found = false;
        foreach (var handle in metadata.GetAssemblyDefinition().GetCustomAttributes())
        {
            var attribute = metadata.GetCustomAttribute(handle);
            if (attribute.Constructor.Kind != HandleKind.MemberReference) continue;
            var ctor = metadata.GetMemberReference((MemberReferenceHandle)attribute.Constructor);
            if (ctor.Parent.Kind != HandleKind.TypeReference) continue;
            var type = metadata.GetTypeReference((TypeReferenceHandle)ctor.Parent);
            if (metadata.GetString(type.Namespace) != "System.Reflection"
                || metadata.GetString(type.Name) != "AssemblyInformationalVersionAttribute") continue;
            if (found) throw Invalid("duplicate informational version attribute");
            found = true;
            if (type.ResolutionScope.Kind != HandleKind.AssemblyReference
                || metadata.GetString(ctor.Name) != ".ctor"
                || !metadata.GetBlobBytes(ctor.Signature).AsSpan().SequenceEqual(new byte[] { 0x20, 1, 1, 0x0e }))
                throw Invalid("malformed informational version constructor");
            var reference = metadata.GetAssemblyReference((AssemblyReferenceHandle)type.ResolutionScope);
            if (metadata.GetString(reference.Name) is not ("System.Runtime" or "System.Private.CoreLib" or "mscorlib"))
                throw Invalid("informational version attribute must reference a framework assembly");
            var blob = metadata.GetBlobReader(attribute.Value);
            if (blob.ReadUInt16() != 1) throw Invalid("malformed informational version attribute prolog");
            result = blob.ReadSerializedString();
            if (blob.ReadUInt16() != 0 || blob.RemainingBytes != 0)
                throw Invalid("malformed informational version attribute payload");
        }
        if (!found) throw Invalid("missing informational version attribute");
        return result;
    }

    private static void CheckVersion(ModuleManifest manifest, string? value)
    {
        ModuleVersion version;
        try { version = ModuleVersion.Parse(value ?? ""); }
        catch (FormatException e) { throw new FormatException("Module discovery: malformed informational version.", e); }
        if (manifest.Version.CompareTo(version) != 0)
            throw Invalid($"informational version '{value}' differs from manifest version '{manifest.Version}'");
    }

    private static ModuleManifest Parse(byte[] bytes)
    {
        try { return ModuleManifest.Parse(StrictUtf8.GetString(bytes)); }
        catch (DecoderFallbackException e) { throw new FormatException("Module discovery: manifest is not valid UTF-8.", e); }
    }

    private static byte[] ReadBounded(Stream stream, int maximum)
    {
        using var output = new MemoryStream();
        var buffer = new byte[4096];
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) != 0)
        {
            if (output.Length + read > maximum) throw Invalid("input exceeds its permitted byte limit");
            output.Write(buffer, 0, read);
        }
        return output.ToArray();
    }

    private static FormatException Invalid(string message) => new("Module discovery: " + message + ".");
}
