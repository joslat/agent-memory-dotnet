using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AgentMemory.Shared;

// Read data from the PE resource directory without loading the assembly or following linked files.
internal static class ManagedEmbeddedResources
{
    internal static byte[] Read(PEReader pe, ManifestResource resource, int maximumBytes, string identity)
    {
        if (!resource.Implementation.IsNil) throw Invalid("must be embedded, not linked externally");
        var directory = pe.PEHeaders.CorHeader!.ResourcesDirectory;
        if (directory.Size < 4 || resource.Offset > directory.Size - 4)
            throw Invalid("resource offset is outside the declared resource directory");
        var section = pe.GetSectionData(directory.RelativeVirtualAddress);
        if (directory.Size > section.Length) throw Invalid("truncated managed resource directory");
        var bytes = section.GetReader(checked((int)resource.Offset), directory.Size - checked((int)resource.Offset));
        var length = bytes.ReadUInt32();
        if (length > maximumBytes || length > bytes.RemainingBytes)
            throw Invalid("resource length is out of bounds");
        return bytes.ReadBytes((int)length);

        FormatException Invalid(string message) => new(identity + ": " + message + ".");
    }
}
