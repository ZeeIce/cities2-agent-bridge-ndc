using System.Reflection;

// Usage:
//   ApiProbe <assemblyDir> [extraDir...] -- find <regex> [assemblyName]
//   ApiProbe <assemblyDir> [extraDir...] -- type <Full.Type.Name> [more types...]
// Loads assemblies for inspection only; nothing is executed from them.
int split = Array.IndexOf(args, "--");
if (split < 1 || args.Length < split + 3) { Console.Error.WriteLine("usage: ApiProbe <dir...> -- find|type ..."); return 2; }
var dirs = args.Take(split).ToArray();
string mode = args[split + 1];
var rest = args.Skip(split + 2).ToArray();

AppDomain.CurrentDomain.AssemblyResolve += (_, e) =>
{
    string name = new AssemblyName(e.Name).Name + ".dll";
    foreach (var d in dirs) { var p = Path.Combine(d, name); if (File.Exists(p)) return Assembly.LoadFrom(p); }
    return null;
};
Assembly Load(string simple)
{
    foreach (var d in dirs) { var p = Path.Combine(d, simple + ".dll"); if (File.Exists(p)) return Assembly.LoadFrom(p); }
    throw new FileNotFoundException(simple);
}
Type[] TypesOf(Assembly a)
{
    try { return a.GetTypes(); }
    catch (ReflectionTypeLoadException e) { return e.Types.Where(t => t != null).ToArray(); }
}
string Name(Type t) => t == null ? "?" : t.IsGenericType
    ? t.Name.Split('`')[0] + "<" + string.Join(",", t.GetGenericArguments().Select(Name)) + ">"
    : (t.FullName ?? t.Name);

if (mode == "find")
{
    var rx = new System.Text.RegularExpressions.Regex(rest[0]);
    var asm = Load(rest.Length > 1 ? rest[1] : "Game");
    foreach (var t in TypesOf(asm).Where(t => t.FullName != null && rx.IsMatch(t.FullName)).OrderBy(t => t.FullName))
    {
        string kind = t.IsEnum ? "enum" : t.IsValueType ? "struct" : t.IsInterface ? "interface" : "class";
        string ifaces = string.Join(",", t.GetInterfaces().Select(i => i.Name).Where(n => n is "IComponentData" or "IBufferElementData" or "ISharedComponentData" or "IEnableableComponent"));
        Console.WriteLine($"{kind} {t.FullName} {ifaces}");
    }
    return 0;
}
if (mode == "type")
{
    var asms = new List<Assembly>();
    foreach (var d in dirs) foreach (var f in Directory.GetFiles(d, "*.dll"))
    {
        string n = Path.GetFileNameWithoutExtension(f);
        if (n.StartsWith("Game") || n.StartsWith("Colossal") || n.Contains("Traffic") || n.Contains("RoadBuilder") || n.Contains("Lane"))
            try { asms.Add(Assembly.LoadFrom(f)); } catch { }
    }
    foreach (var wanted in rest)
    {
        var t = asms.Select(a => { try { return a.GetType(wanted); } catch { return null; } }).FirstOrDefault(x => x != null);
        if (t == null) { Console.WriteLine($"== {wanted}: NOT FOUND"); continue; }
        Console.WriteLine($"== {t.FullName} [{t.Assembly.GetName().Name}] base={Name(t.BaseType)} ifaces={string.Join(",", t.GetInterfaces().Select(i => i.Name))}");
        if (t.IsEnum) { Console.WriteLine("   values: " + string.Join(", ", Enum.GetNames(t))); continue; }
        const BindingFlags all = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
        foreach (var f in t.GetFields(all)) Console.WriteLine($"   field {(f.IsPublic ? "public" : "private")}{(f.IsStatic ? " static" : "")} {Name(f.FieldType)} {f.Name}");
        foreach (var p in t.GetProperties(all)) Console.WriteLine($"   prop {Name(p.PropertyType)} {p.Name} {(p.GetMethod?.IsPublic == true ? "get" : "")}{(p.SetMethod?.IsPublic == true ? " set" : "")}");
        foreach (var m in t.GetMethods(all).Where(m => !m.IsSpecialName && (m.IsPublic || rest.Contains("--private"))))
            Console.WriteLine($"   method {(m.IsStatic ? "static " : "")}{Name(m.ReturnType)} {m.Name}({string.Join(", ", m.GetParameters().Select(p => Name(p.ParameterType) + " " + p.Name))})");
    }
    return 0;
}
Console.Error.WriteLine("unknown mode"); return 2;
