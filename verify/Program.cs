using Mono.Cecil;
using Silksong43.Verify;

// verify <patched.dll> [original.dll] [managedDir]
// loads the patched dll, runs the il, semantic and simulator layers, exits non-zero on any failure.
// run from the repo root. original defaults to original/Managed/Assembly-CSharp.dll, managedDir to original/Managed.
var pos = args.Where(a => !a.StartsWith('-')).ToArray();
string patchedPath = Path.GetFullPath(pos.Length > 0 ? pos[0] : "Assembly-CSharp-43.dll");
string origPath = Path.GetFullPath(pos.Length > 1 ? pos[1] : "original/Managed/Assembly-CSharp.dll");
string managed = Path.GetFullPath(pos.Length > 2 ? pos[2] : "original/Managed");
if (!File.Exists(patchedPath)) { Console.Error.WriteLine($"verify: missing dll '{patchedPath}'"); return 2; }
if (!File.Exists(origPath)) { Console.Error.WriteLine($"verify: missing original '{origPath}'"); return 2; }

var mode = args.Contains("--reference-free") ? VerifyMode.ReferenceFree : VerifyMode.Full;
var resolver = new DefaultAssemblyResolver();
if (Directory.Exists(managed)) resolver.AddSearchDirectory(managed);
else if (mode == VerifyMode.Full) { Console.Error.WriteLine($"verify: managed dir '{managed}' not found"); return 2; }

var checks = new Checks();
Console.WriteLine($"silksong 43-aspect verification: {Path.GetFileName(patchedPath)} ({mode})");
Console.WriteLine($"  sha256 {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(patchedPath))).ToLowerInvariant()}");
using var module = ModuleDefinition.ReadModule(patchedPath, new ReaderParameters { AssemblyResolver = resolver });
VerifyRunner.Run(checks, module, mode, File.ReadAllBytes(origPath));
checks.Summarize();
return checks.Failed == 0 ? 0 : 1;
