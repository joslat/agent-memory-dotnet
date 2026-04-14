using System;
using System.Reflection;
using System.Linq;
using System.IO;

var dllPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".nuget", "packages", "microsoft.agents.ai.abstractions", "1.1.0", "lib", "net9.0", "Microsoft.Agents.AI.Abstractions.dll");

if (!File.Exists(dllPath))
{
    Console.WriteLine($"DLL not found at: {dllPath}");
    // Try listing what's there
    var baseDir = Path.GetDirectoryName(Path.GetDirectoryName(dllPath));
    if (Directory.Exists(baseDir))
    {
        foreach (var d in Directory.GetDirectories(baseDir))
            Console.WriteLine($"  {d}");
    }
    return;
}

var asm = Assembly.LoadFrom(dllPath);
Console.WriteLine($"Assembly: {asm.FullName}");
Console.WriteLine();

var types = asm.GetExportedTypes().OrderBy(t => t.Namespace).ThenBy(t => t.Name);
foreach (var t in types)
{
    var kind = t.IsInterface ? "interface" : t.IsEnum ? "enum" : t.IsAbstract ? "abstract class" : t.IsClass ? "class" : "struct";
    var baseType = t.BaseType != null && t.BaseType != typeof(object) && t.BaseType != typeof(ValueType) && t.BaseType != typeof(Enum)
        ? $" : {t.BaseType.Name}" : "";
    var ifaces = t.GetInterfaces().Where(i => i != typeof(IDisposable)).Select(i => i.Name);
    Console.WriteLine($"[{t.Namespace}] {kind} {t.Name}{baseType}");

    // Show members for key types
    if (t.Name == "AIContextProvider" || t.Name == "InvokingContext" || t.Name == "AIContext")
    {
        Console.WriteLine("  --- Properties ---");
        foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            Console.WriteLine($"    {p.PropertyType.Name} {p.Name} {{ {(p.CanRead?"get;":"")}{(p.CanWrite?"set;":"")} }}");

        Console.WriteLine("  --- Methods ---");
        foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly | BindingFlags.NonPublic)
            .Where(m => !m.IsSpecialName))
        {
            var vis = m.IsPublic ? "public" : m.IsFamily ? "protected" : m.IsPrivate ? "private" : "internal";
            var virt = m.IsVirtual ? "virtual " : "";
            var abs = m.IsAbstract ? "abstract " : "";
            var parms = string.Join(", ", m.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"    {vis} {abs}{virt}{m.ReturnType.Name} {m.Name}({parms})");
        }

        Console.WriteLine("  --- Constructors ---");
        foreach (var c in t.GetConstructors(BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic))
        {
            var vis = c.IsPublic ? "public" : c.IsFamily ? "protected" : "private";
            var parms = string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"));
            Console.WriteLine($"    {vis} {t.Name}({parms})");
        }
    }
}
