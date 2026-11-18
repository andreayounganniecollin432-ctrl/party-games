using System.Reflection;
using System.Runtime.InteropServices;

// Dumps real member signatures out of Dalamud.dll so plugin code is written
// against what the assemblies actually expose, not against memory.
var distDir = args.Length > 0 ? args[0] : @"C:\Coral\_dl\dalamud-distrib\extracted";

// Optional: "ApiDump <dir> <Some.Assembly.dll> <Type>..." to inspect a sibling assembly.
var assemblyFile = "Dalamud.dll";
var typeArgs = args.Skip(1).ToArray();
if (typeArgs.Length > 0 && typeArgs[0].EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
{
    assemblyFile = typeArgs[0];
    typeArgs = typeArgs.Skip(1).ToArray();
}

var wanted = typeArgs;

if (wanted.Length == 0)
{
    Console.WriteLine("usage: ApiDump <dalamud-dir> [assembly.dll] <TypeName> [TypeName...]");
    return 1;
}

var runtimeDir = RuntimeEnvironment.GetRuntimeDirectory();
var paths = Directory.GetFiles(distDir, "*.dll")
    .Concat(Directory.GetFiles(runtimeDir, "*.dll"))
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

using var context = new MetadataLoadContext(new PathAssemblyResolver(paths));
var assembly = context.LoadFromAssemblyPath(Path.Combine(distDir, assemblyFile));

Type[] all;
try
{
    all = assembly.GetTypes();
}
catch (ReflectionTypeLoadException ex)
{
    all = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
}

foreach (var name in wanted)
{
    var type = all.FirstOrDefault(t => t.Name == name || t.FullName == name);
    if (type is null)
    {
        Console.WriteLine($"### {name}: NOT FOUND");
        continue;
    }

    Console.WriteLine($"### {type.FullName}");
    if (type.BaseType is not null) Console.WriteLine($"    base: {type.BaseType.FullName}");

    foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly))
    {
        Console.WriteLine($"    prop  {property.PropertyType.Name,-40} {property.Name}");
    }

    foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                                   .Where(m => !m.IsSpecialName)
                                   .OrderBy(m => m.Name))
    {
        var parameters = string.Join(", ", method.GetParameters().Select(p => $"{Short(p.ParameterType)} {p.Name}"));
        Console.WriteLine($"    meth  {Short(method.ReturnType),-30} {method.Name}({parameters})");
    }

    Console.WriteLine();
}

return 0;

static string Short(Type type)
{
    if (type.IsGenericType)
    {
        var name = type.Name[..type.Name.IndexOf('`')];
        var inner = string.Join(", ", type.GetGenericArguments().Select(Short));
        return $"{name}<{inner}>";
    }

    return type.Name switch
    {
        "Void" => "void",
        "String" => "string",
        "Int32" => "int",
        "Boolean" => "bool",
        "Single" => "float",
        "UInt32" => "uint",
        _ => type.Name,
    };
}
