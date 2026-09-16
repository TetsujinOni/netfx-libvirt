using NetfxLibvirt.ProtocolGen.Ast;

namespace NetfxLibvirt.ProtocolGen.Semantics;

/// <summary>Builds a resolved <see cref="XdlModule"/> from a raw, unresolved
/// <see cref="XdlSpecification"/>.</summary>
public static class XdlModuleBuilder
{
    public static XdlModule Build(XdlSpecification spec)
    {
        var constants = new XdlConstantTable();
        var structs = new Dictionary<string, XdlStructDefinition>();
        var enums = new Dictionary<string, XdlEnumDefinition>();
        var unions = new Dictionary<string, XdlUnionDefinition>();
        var typedefs = new Dictionary<string, XdlDeclaration>();

        foreach (var definition in spec.Definitions)
        {
            switch (definition)
            {
                case XdlConstDefinition c:
                    constants.Add(c.Name, c.Value);
                    break;
                case XdlStructDefinition s:
                    structs[s.Name] = s;
                    break;
                case XdlEnumDefinition e:
                    enums[e.Name] = e;
                    break;
                case XdlUnionDefinition u:
                    unions[u.Name] = u;
                    break;
                case XdlTypedefDefinition t:
                    typedefs[t.Declaration.Name] = t.Declaration;
                    break;
                case XdlProgramDefinition:
                    // Grammar completeness only — see XdlProcedure's class doc.
                    // Nothing to index; libvirt's own .x files don't use this.
                    break;
            }
        }

        var procedures = ExtractProcedures(enums.Values, structs, constants);

        return new XdlModule
        {
            Constants = constants,
            Structs = structs,
            Enums = enums,
            Unions = unions,
            Typedefs = typedefs,
            Procedures = procedures,
        };
    }

    private static List<XdlProcedure> ExtractProcedures(
        IEnumerable<XdlEnumDefinition> enums,
        IReadOnlyDictionary<string, XdlStructDefinition> structs,
        XdlConstantTable constants)
    {
        var procedures = new List<XdlProcedure>();
        foreach (var enumDef in enums)
        {
            foreach (var value in enumDef.Values)
            {
                if (!value.IsProcedure)
                {
                    continue;
                }

                // "<PROGRAM>_PROC_<REST>" -> program/rest split, matching the
                // lexer's own procIdent heuristic (XdlLexer.IsProcIdentifier).
                var procIndex = value.Name.IndexOf("_PROC_", StringComparison.Ordinal);
                var program = value.Name[..procIndex].ToLowerInvariant();
                var rest = value.Name[(procIndex + "_PROC_".Length)..].ToLowerInvariant();

                var argsStructName = $"{program}_{rest}_args";
                var retStructName = $"{program}_{rest}_ret";

                procedures.Add(new XdlProcedure(
                    value.Name,
                    constants.Resolve(value.Value!),
                    value.MetadataComment,
                    structs.ContainsKey(argsStructName) ? argsStructName : null,
                    structs.ContainsKey(retStructName) ? retStructName : null));
            }
        }

        return procedures;
    }
}
