using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Runtime.Loader;
using System.Text;
using AgentMemory.Extensibility.Modules;
using FluentAssertions;
using Xunit;

namespace AgentMemory.Tests.Unit.Extensibility;

public sealed class ModuleAssemblyTests
{
    private const string Manifest = """
        {"format":"agentmemory.module/v1alpha1","id":"com.example.discovery","version":"0.1.0","sdk":"[0.1,0.2)"}
        """;

    [Fact]
    public void InspectsRealCompiledAssemblyWithoutLoadingItOrRunningItsInitializer()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "ModuleFixtures", "ModuleDiscoveryFixture.dll");
        AppDomain.CurrentDomain.GetAssemblies().Should().NotContain(a => a.GetName().Name == "ModuleDiscoveryFixture");
        var m = ModuleManifest.LoadFromAssemblyFile(path);
        m.Id.Should().Be("com.example.discovery");
        m.Version.Value.Should().Be("0.1.0+manifest-build", "build metadata does not alter the required version agreement");
        AppDomain.CurrentDomain.GetAssemblies().Should().NotContain(a => a.GetName().Name == "ModuleDiscoveryFixture");

        // A positive execution control proves the compiled fixture really has a live tripwire.
        var context = new AssemblyLoadContext("module-tripwire-control", isCollectible: true);
        try
        {
            using var stream = File.OpenRead(path);
            var loaded = context.LoadFromStream(stream);
            ModuleManifest.Load(loaded).Id.Should().Be(m.Id, "already-loaded metadata access must not instantiate attributes");
            Action execute = () => loaded.GetType("ModuleDiscoveryFixture.Tripwire")!.GetMethod("Ping")!.Invoke(null, null);
            execute.Should().Throw<Exception>().Where(e => e.ToString().Contains("MODULE_EXECUTED", StringComparison.Ordinal));
        }
        finally { context.Unload(); }
    }

    [Theory]
    [InlineData("0.1.0")]
    [InlineData("0.1.0+build.42")]
    public void AcceptsOneEmbeddedManifestAndMatchingInformationalVersion(string version)
    {
        var image = Image(version, ("fixture.agentmemory-module.json", Encoding.UTF8.GetBytes(Manifest)));
        ModuleManifest.LoadFromAssemblyImage(image).Id.Should().Be("com.example.discovery");
    }

    [Theory]
    [InlineData("0.1.1")]
    [InlineData("0.1.0-preview")]
    [InlineData("0.1")]
    [InlineData(null)]
    public void RefusesMissingMalformedOrDifferentInformationalVersion(string? version)
    {
        var image = Image(version, ("fixture.agentmemory-module.json", Encoding.UTF8.GetBytes(Manifest)));
        FluentActions.Invoking(() => ModuleManifest.LoadFromAssemblyImage(image)).Should().Throw<FormatException>()
            .WithMessage("*informational*version*");
    }

    [Fact]
    public void IgnoresUnrelatedResourcesButRefusesNoManifestOrMultipleManifests()
    {
        ModuleManifest.LoadFromAssemblyImage(Image("0.1.0", ("readme.txt", [1,2,3]),
            ("a.agentmemory-module.json", Encoding.UTF8.GetBytes(Manifest)))).Id.Should().Be("com.example.discovery");
        foreach (var resources in new[] { System.Array.Empty<(string, byte[])>(),
            new[] { ("a.agentmemory-module.json", Encoding.UTF8.GetBytes(Manifest)), ("b.agentmemory-module.json", Encoding.UTF8.GetBytes(Manifest)) } })
            FluentActions.Invoking(() => ModuleManifest.LoadFromAssemblyImage(Image("0.1.0", resources)))
                .Should().Throw<FormatException>().WithMessage("*exactly one*manifest*");
    }

    [Fact]
    public void RefusesInvalidUtf8AndOversizeResourceBeforeParsing()
    {
        foreach (var payload in new[] { new byte[] { 0xff, 0xfe, 0xfd }, new byte[ModuleManifest.MaximumUtf8Bytes + 1] })
            FluentActions.Invoking(() => ModuleManifest.LoadFromAssemblyImage(Image("0.1.0", ("a.agentmemory-module.json", payload))))
                .Should().Throw<FormatException>();
    }

    [Fact]
    public void RefusesUnmanagedTruncatedAndOversizeImages()
    {
        foreach (var image in new[] { new byte[] { 1,2,3 }, new byte[ModuleManifest.MaximumAssemblyBytes + 1],
            Image("0.1.0", ("a.agentmemory-module.json", Encoding.UTF8.GetBytes(Manifest)))[..128] })
            FluentActions.Invoking(() => ModuleManifest.LoadFromAssemblyImage(image)).Should().Throw<FormatException>();
    }

    [Fact]
    public void RejectsInvalidResourceOffsetAndLengthAsDataErrors()
    {
        var original = Image("0.1.0", ("a.agentmemory-module.json", Encoding.UTF8.GetBytes(Manifest)));
        using var reader = new PEReader(new MemoryStream(original));
        var directory = reader.PEHeaders.CorHeader!.ResourcesDirectory;
        var section = reader.PEHeaders.SectionHeaders.Single(s => directory.RelativeVirtualAddress >= s.VirtualAddress
            && directory.RelativeVirtualAddress < s.VirtualAddress + s.VirtualSize);
        var lengthPosition = section.PointerToRawData + directory.RelativeVirtualAddress - section.VirtualAddress;
        foreach (var length in new uint[] { uint.MaxValue, (uint)directory.Size + 1 })
        {
            var bytes = original.ToArray();
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(lengthPosition, 4), length);
            FluentActions.Invoking(() => ModuleManifest.LoadFromAssemblyImage(bytes)).Should().Throw<FormatException>();
        }
    }

    [Fact]
    public void RefusesDuplicateVersionAttributesEvenWhenTheyAgree()
    {
        var image = Image("0.1.0", new[] { ("a.agentmemory-module.json", Encoding.UTF8.GetBytes(Manifest)) }, duplicateVersion: true);
        FluentActions.Invoking(() => ModuleManifest.LoadFromAssemblyImage(image)).Should().Throw<FormatException>()
            .WithMessage("*duplicate informational version*");
    }

    [Fact]
    public void RefusesLinkedManifestWithoutFollowingItsFileReference()
    {
        var image = Image("0.1.0", new[] { ("a.agentmemory-module.json", Encoding.UTF8.GetBytes(Manifest)) }, linked: true);
        FluentActions.Invoking(() => ModuleManifest.LoadFromAssemblyImage(image)).Should().Throw<FormatException>()
            .WithMessage("*embedded*externally*");
    }

    [Fact]
    public void RefusesResourceOffsetBeyondTheDirectory()
    {
        var image = Image("0.1.0", new[] { ("a.agentmemory-module.json", Encoding.UTF8.GetBytes(Manifest)) }, offset: uint.MaxValue);
        FluentActions.Invoking(() => ModuleManifest.LoadFromAssemblyImage(image)).Should().Throw<FormatException>()
            .WithMessage("*offset*outside*");
    }

    internal static byte[] Image(string? version, params (string Name, byte[] Bytes)[] files) =>
        Image(version, files, duplicateVersion: false);

    internal static byte[] Image(string? version, (string Name, byte[] Bytes)[] files,
        bool duplicateVersion = false, bool linked = false, uint? offset = null, string? linkedResource = null)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("MetadataFixture.dll"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddAssembly(metadata.GetOrAddString("MetadataFixture"), new Version(0,1,0,0), default, default, 0, AssemblyHashAlgorithm.None);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        if (version is not null)
        {
            var runtime = metadata.AddAssemblyReference(metadata.GetOrAddString("System.Runtime"), new Version(8,0,0,0), default,
                metadata.GetOrAddBlob(Convert.FromHexString("B03F5F7F11D50A3A")), 0, default);
            var attribute = metadata.AddTypeReference(runtime, metadata.GetOrAddString("System.Reflection"),
                metadata.GetOrAddString("AssemblyInformationalVersionAttribute"));
            var ctor = metadata.AddMemberReference(attribute, metadata.GetOrAddString(".ctor"), metadata.GetOrAddBlob(new byte[] { 0x20,1,1,0x0e }));
            var blob = new BlobBuilder();blob.WriteUInt16(1);blob.WriteSerializedString(version);blob.WriteUInt16(0);
            metadata.AddCustomAttribute(MetadataTokens.EntityHandle(0x20000001), ctor, metadata.GetOrAddBlob(blob));
            if (duplicateVersion) metadata.AddCustomAttribute(MetadataTokens.EntityHandle(0x20000001), ctor, metadata.GetOrAddBlob(blob));
        }
        var resources = new BlobBuilder();
        foreach (var file in files)
        {
            EntityHandle implementation = linked || file.Name == linkedResource
                ? metadata.AddAssemblyFile(metadata.GetOrAddString("must-not-follow.resources"), default, containsMetadata: false) : default;
            metadata.AddManifestResource(ManifestResourceAttributes.Public, metadata.GetOrAddString(file.Name), implementation, offset ?? (uint)resources.Count);
            resources.WriteUInt32((uint)file.Bytes.Length);resources.WriteBytes(file.Bytes);resources.Align(8);
        }
        var pe = new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics: Characteristics.ExecutableImage | Characteristics.Dll),
            new MetadataRootBuilder(metadata), new BlobBuilder(), managedResources: resources);
        var output = new BlobBuilder();pe.Serialize(output);return output.ToArray();
    }
}
