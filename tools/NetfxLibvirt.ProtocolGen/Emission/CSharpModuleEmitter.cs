using NetfxLibvirt.ProtocolGen.Semantics;

namespace NetfxLibvirt.ProtocolGen.Emission;

/// <summary>Emits one C# file per struct/enum reachable from a set of root
/// struct names, keyed by the filename it should be written to (matching
/// this repo's one-type-per-file convention).</summary>
public static class CSharpModuleEmitter
{
    public static IReadOnlyDictionary<string, string> Emit(XdlModule module, IEnumerable<string> rootStructNames)
    {
        var closure = XdlClosureCollector.Collect(rootStructNames, module);
        var files = new Dictionary<string, string>();

        foreach (var enumDef in closure.Enums)
        {
            files[$"{CSharpNaming.ToPascalCase(enumDef.Name)}.cs"] = CSharpTypeEmitter.EmitEnum(enumDef, module);
        }

        foreach (var structDef in closure.Structs)
        {
            files[$"{CSharpNaming.ToPascalCase(structDef.Name)}.cs"] = CSharpTypeEmitter.EmitStruct(structDef, module);
        }

        return files;
    }
}
