// Usage: Il2CppDump <outDir> <TypeName> [TypeName...]
// Type names match TypeAnalysisContext.FullName exactly, or "Name*" as a prefix (also matches nested types).
using System;
using System.IO;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Cpp2IL.Core;
using Cpp2IL.Core.Model.Contexts;

var gameDir = Environment.GetEnvironmentVariable("GAME_DIR")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local/share/Steam/steamapps/common/Supermarket Simulator");
var dataDir = Path.Combine(gameDir, "Supermarket Simulator_Data");
var outDir = args[0];
var filters = args.Skip(1).ToArray();
Directory.CreateDirectory(outDir);

Cpp2IlApi.Init(); // registers the built-in core plugin (x86_64 instruction set etc.)
var version = Cpp2IlApi.DetermineUnityVersion(Path.Combine(gameDir, "UnityPlayer.dll"), dataDir);
Console.Error.WriteLine($"Unity {version}");
Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(gameDir, "GameAssembly.dll"),
    Path.Combine(dataDir, "il2cpp_data/Metadata/global-metadata.dat"), version);
var ctx = Cpp2IlApi.CurrentAppContext!;

// A filter also matches every type nested (at any depth) inside the named type.
// Optional DUMP_ASSEMBLY env var restricts to one assembly (e.g. Assembly-CSharp); filter "*" = all types.
var asmFilter = Environment.GetEnvironmentVariable("DUMP_ASSEMBLY");
bool Match(TypeAnalysisContext t) => (asmFilter == null || t.DeclaringAssembly.CleanAssemblyName == asmFilter) && filters.Any(f =>
{
    for (var cur = t; cur != null; cur = cur.DeclaringType)
        if (f.EndsWith('*') ? cur.FullName.StartsWith(f[..^1]) : cur.FullName == f) return true;
    return false;
});

foreach (var type in ctx.AllTypes.Where(Match))
{
    var sb = new StringBuilder();
    sb.AppendLine($"Type: {type.FullName}  (base: {type.BaseType?.FullName})");
    sb.AppendLine("Fields:");
    foreach (var f in type.Fields)
        sb.AppendLine($"  0x{f.BackingData?.FieldOffset ?? -1:X}\t{(f.IsStatic ? "static " : "")}{f.FieldType.FullName} {f.Name}");
    sb.AppendLine();
    foreach (var m in type.Methods)
    {
        sb.AppendLine($"=== {m.FullNameWithSignature}   RVA=0x{m.Rva:X}");
        try
        {
            sb.AppendLine(ctx.InstructionSet.PrintAssembly(m));
            m.Analyze();
            sb.AppendLine("--- ISIL");
            foreach (var i in m.ConvertedIsil!) sb.Append('\t').Append(i).AppendLine();
            m.ReleaseAnalysisData();
        }
        catch (Exception e) { sb.AppendLine($"!! {e.GetType().Name}: {e.Message}"); }
        sb.AppendLine();
    }
    var name = string.Join("+", Chain(type)).Replace('/', '+');
    foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
    File.WriteAllText(Path.Combine(outDir, name + ".txt"), sb.ToString());
    Console.Error.WriteLine($"dumped {type.FullName} ({type.Methods.Count} methods)");
}

static IEnumerable<string> Chain(TypeAnalysisContext t)
{
    var names = new List<string>();
    for (var cur = t; cur != null; cur = cur.DeclaringType) names.Insert(0, cur.DeclaringType == null ? cur.FullName : cur.Name);
    return names;
}
