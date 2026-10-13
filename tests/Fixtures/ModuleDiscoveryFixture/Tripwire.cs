using System.Runtime.CompilerServices;

[assembly: ModuleDiscoveryFixture.TripwireAttribute]

namespace ModuleDiscoveryFixture;

[AttributeUsage(AttributeTargets.Assembly)]
public sealed class TripwireAttribute : Attribute
{
    public TripwireAttribute() => throw new InvalidOperationException("ATTRIBUTE_EXECUTED");
}

public static class Tripwire
{
    [ModuleInitializer]
    internal static void Initialize() => throw new InvalidOperationException("MODULE_EXECUTED");
    public static void Ping() { }
}
