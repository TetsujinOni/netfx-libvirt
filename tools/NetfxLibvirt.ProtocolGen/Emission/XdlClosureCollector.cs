using NetfxLibvirt.ProtocolGen.Ast;
using NetfxLibvirt.ProtocolGen.Semantics;

namespace NetfxLibvirt.ProtocolGen.Emission;

/// <summary>The set of struct/enum definitions reachable from a set of root
/// struct names, in dependency order (a struct's own fields are visited —
/// and so appear earlier in the list — before the struct itself, so nothing
/// emitted needs a forward reference).</summary>
public sealed record XdlClosure(IReadOnlyList<XdlStructDefinition> Structs, IReadOnlyList<XdlEnumDefinition> Enums);

/// <summary>Walks field shapes (through arrays and optionals) to find every
/// struct/enum a root type transitively depends on. Unions aren't supported
/// yet — none of the MVP procedure set's args/ret structs reach one; see
/// <c>XdlParser</c>'s own scope note for the same "prove the subset first"
/// reasoning applied one layer up.</summary>
public static class XdlClosureCollector
{
    public static XdlClosure Collect(IEnumerable<string> rootStructNames, XdlModule module)
    {
        var visitedStructs = new HashSet<string>();
        var visitedEnums = new HashSet<string>();
        var structOrder = new List<XdlStructDefinition>();
        var enumOrder = new List<XdlEnumDefinition>();

        void VisitStruct(string name)
        {
            if (!visitedStructs.Add(name))
            {
                return;
            }

            var def = module.Structs[name];
            foreach (var field in def.Fields)
            {
                VisitShape(XdlTypeResolver.ResolveDeclaration(field, module));
            }

            structOrder.Add(def);
        }

        void VisitShape(XdlTypeShape shape)
        {
            switch (shape)
            {
                case XdlStructShape s:
                    VisitStruct(s.Definition.Name);
                    break;
                case XdlEnumShape e:
                    if (visitedEnums.Add(e.Definition.Name))
                    {
                        enumOrder.Add(e.Definition);
                    }

                    break;
                case XdlUnionShape u:
                    throw new NotSupportedException(
                        $"emitting union '{u.Definition.Name}' isn't supported yet — none of the MVP procedures need one; see XdlClosureCollector's class doc.");
                case XdlOptionalShape opt:
                    VisitShape(opt.Inner);
                    break;
                case XdlFixedListShape fl:
                    VisitShape(fl.Element);
                    break;
                case XdlBoundedListShape bl:
                    VisitShape(bl.Element);
                    break;
            }
        }

        foreach (var rootName in rootStructNames)
        {
            VisitStruct(rootName);
        }

        return new XdlClosure(structOrder, enumOrder);
    }
}
