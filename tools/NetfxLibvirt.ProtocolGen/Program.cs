using NetfxLibvirt.ProtocolGen.Emission;
using NetfxLibvirt.ProtocolGen.Parsing;
using NetfxLibvirt.ProtocolGen.Semantics;

// Dev-time CLI: regenerates src/NetfxLibvirt/Generated/Remote from the
// vendored reference/upstream-x/remote_protocol.x. Run with `dotnet run
// --project tools/NetfxLibvirt.ProtocolGen` from the repo root, or pass the
// repo root explicitly as the first argument.
//
// Scoped to the MVP procedure set from docs/status.md on purpose — the
// parser and semantic layer generalize to the full ~200+ procedure file,
// but the emitter hasn't been proven against the rest of it yet (per this
// project's own "prove a subset first" methodology, applied one layer up
// from the parser).
var repoRoot = args.Length > 0 ? args[0] : FindRepoRoot(AppContext.BaseDirectory);
var protocolPath = Path.Combine(repoRoot, "reference", "upstream-x", "remote_protocol.x");
var outputDir = Path.Combine(repoRoot, "src", "NetfxLibvirt", "Generated", "Remote");

string[] mvpProcedures =
[
    "REMOTE_PROC_CONNECT_OPEN",
    "REMOTE_PROC_CONNECT_GET_CAPABILITIES",
    "REMOTE_PROC_CONNECT_LIST_ALL_DOMAINS",
    "REMOTE_PROC_DOMAIN_GET_INFO",
    "REMOTE_PROC_DOMAIN_GET_XML_DESC",
    "REMOTE_PROC_DOMAIN_CREATE",
    "REMOTE_PROC_DOMAIN_SHUTDOWN",
    "REMOTE_PROC_DOMAIN_DESTROY",
    "REMOTE_PROC_AUTH_LIST",
];

var module = XdlModuleBuilder.Build(XdlParser.Parse(File.ReadAllText(protocolPath)));

var rootStructNames = mvpProcedures
    .Select(name => module.Procedures.Single(p => p.Name == name))
    .SelectMany(proc => new[] { proc.ArgsStructName, proc.RetStructName })
    .OfType<string>()
    .Distinct()
    .ToList();

var files = CSharpModuleEmitter.Emit(module, rootStructNames);

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
