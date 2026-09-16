using NetfxLibvirt.ProtocolGen.Emission;
using NetfxLibvirt.ProtocolGen.Parsing;
using NetfxLibvirt.ProtocolGen.Semantics;

// Dev-time CLI: regenerates src/NetfxLibvirt/Generated/Remote from the
// vendored reference/upstream-x/remote_protocol.x. Run with `dotnet run
// --project tools/NetfxLibvirt.ProtocolGen` from the repo root, or pass the
// repo root explicitly as the first argument.
//
// Scoped to the parity-with-virt-desktop procedure set from docs/plan.md on
// purpose — the parser and semantic layer generalize to the full ~200+
// procedure file, but the emitter hasn't been proven against the rest of it
// yet (per this project's own "prove a subset first" methodology, applied
// one layer up from the parser).
var repoRoot = args.Length > 0 ? args[0] : FindRepoRoot(AppContext.BaseDirectory);
var protocolPath = Path.Combine(repoRoot, "reference", "upstream-x", "remote_protocol.x");
var outputDir = Path.Combine(repoRoot, "src", "NetfxLibvirt", "Generated", "Remote");

string[] procedureSet =
[
    "REMOTE_PROC_CONNECT_OPEN",
    "REMOTE_PROC_CONNECT_CLOSE",
    "REMOTE_PROC_CONNECT_GET_CAPABILITIES",
    "REMOTE_PROC_CONNECT_LIST_ALL_DOMAINS",
    "REMOTE_PROC_DOMAIN_GET_INFO",
    "REMOTE_PROC_DOMAIN_GET_STATE",
    "REMOTE_PROC_DOMAIN_GET_XML_DESC",
    "REMOTE_PROC_DOMAIN_LOOKUP_BY_NAME",
    "REMOTE_PROC_DOMAIN_CREATE",
    "REMOTE_PROC_DOMAIN_SHUTDOWN",
    "REMOTE_PROC_DOMAIN_DESTROY",
    "REMOTE_PROC_AUTH_LIST",
];

var module = XdlModuleBuilder.Build(XdlParser.Parse(File.ReadAllText(protocolPath)));

var rootStructNames = procedureSet
    .Select(name => module.Procedures.Single(p => p.Name == name))
    .SelectMany(proc => new[] { proc.ArgsStructName, proc.RetStructName })
    .OfType<string>()
    // remote_error is every VIR_NET_ERROR reply's payload — not any
    // procedure's own args/ret struct, so it's never reached by walking the
    // procedure set above. Added explicitly so the RPC call engine (plan.md
    // story 4) has a decoded error shape to throw from.
    .Append("remote_error")
    .Distinct()
    .ToList();

var files = CSharpModuleEmitter.Emit(module, rootStructNames);
(string Name, long Value)[] protocolConstants =
[
    ("REMOTE_PROGRAM", module.Constants.Values["REMOTE_PROGRAM"]),
    ("REMOTE_PROTOCOL_VERSION", module.Constants.Values["REMOTE_PROTOCOL_VERSION"]),
];
files = new Dictionary<string, string>(files)
{
    ["RemoteProcedure.cs"] = CSharpTypeEmitter.EmitProcedureRegistry(module.Procedures, "RemoteProcedure"),
    ["RemoteProtocolConstants.cs"] = CSharpTypeEmitter.EmitConstants(protocolConstants, "RemoteProtocolConstants"),
};

Directory.CreateDirectory(outputDir);
foreach (var existing in Directory.GetFiles(outputDir, "*.cs"))
{
    File.Delete(existing);
}

foreach (var (fileName, content) in files)
{
    File.WriteAllText(Path.Combine(outputDir, fileName), content);
}

Console.WriteLine($"Wrote {files.Count} files to {outputDir}");

static string FindRepoRoot(string startDir)
{
    var dir = new DirectoryInfo(startDir);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "netfx-libvirt.slnx")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName ?? throw new InvalidOperationException($"could not locate repo root (netfx-libvirt.slnx) above {startDir}");
}
