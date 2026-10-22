using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Silksong43.Verify;
using Mono.Cecil;

/// result of one PatchApi.Patch call. Output is non-null only when Reason is "ok", which
/// also means the verifier ran on exactly these bytes and Failed is 0.
public sealed class PatchResult
{
    /// the patched assembly, or null when nothing should be written
    public byte[]? Output { get; init; }
    /// "ok" | "already_patched" | "not_silksong" | "verify_failed" | "error"
    public string Reason { get; init; } = "error";
    /// the patcher's progress log (what the cli prints)
    public string Log { get; init; } = "";
    /// every line the verifier printed, one per check
    public string VerifyLog { get; init; } = "";
    public int Passed { get; init; }
    public int Failed { get; init; }
    /// checks that could not run in this VerifyMode (always 0 in Full)
    public int Skipped { get; init; }
    public string[] Failures { get; init; } = Array.Empty<string>();
    public string Version { get; init; } = PatchApi.Version;
    public VerifyMode Mode { get; init; }
    public long PatchMillis { get; init; }
    public long VerifyMillis { get; init; }
    /// sha256 of Output as lowercase hex, or null
    public string? Sha256 { get; init; }

    public bool Ok => Reason == "ok";
    public string Summary => Checks.FormatSummary(Passed, Failed, Skipped);
}

/// an assembly resolver that resolves nothing. the patcher never needs a unity assembly
/// (its unity calls are built by hand), so this is what it reads with when the host has none.
/// anything that does try to resolve fails loudly instead of quietly binding to the host runtime.
public sealed class NullAssemblyResolver : IAssemblyResolver
{
    public AssemblyDefinition Resolve(AssemblyNameReference name) =>
        throw new AssemblyResolutionException(name);

    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) =>
        throw new AssemblyResolutionException(name);

    public void Dispose() { }
}

/// resolver used only while reading and writing the input. Cecil must resolve an enum's underlying type
/// when it writes a constant or attribute argument of that enum type (optional parameters such as
/// LoadSceneMode.Single). The browser has no Unity dlls, so enums from other assemblies are
/// synthesised on demand: an empty assembly per name, plus a stub enum type when Cecil asks for one.
/// Stubs are never written out; they only answer "is this an enum and what is it built on".
public sealed class StubResolver : IAssemblyResolver
{
    private readonly Dictionary<string, AssemblyDefinition> stubs = new();
    private readonly Dictionary<string, Type> constantTypes = new();
    public int Retries { get; private set; }

    public AssemblyDefinition Resolve(AssemblyNameReference name)
    {
        if (!stubs.TryGetValue(name.Name, out var a))
        {
            a = AssemblyDefinition.CreateAssembly(new AssemblyNameDefinition(name.Name, name.Version ?? new Version(0, 0, 0, 0)), name.Name, ModuleKind.Dll);
            stubs[name.Name] = a;
        }
        return a;
    }

    public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) => Resolve(name);
    public void Dispose() { }

    /// remembers the CLR type of each enum-typed constant so the stub's underlying type matches it
    public void Scan(ModuleDefinition m)
    {
        foreach (var t in m.GetTypes())
        {
            foreach (var f in t.Fields) Note(f.FieldType, f.HasConstant ? f.Constant : null);
            foreach (var me in t.Methods) foreach (var p in me.Parameters) Note(p.ParameterType, p.HasConstant ? p.Constant : null);
        }
    }

    private void Note(TypeReference type, object? constant)
    {
        if (constant == null || type.IsPrimitive || type.MetadataType == MetadataType.String || type.MetadataType == MetadataType.Object) return;
        if (type.MetadataType != MetadataType.ValueType) return;
        constantTypes[type.FullName] = constant.GetType();
    }

    /// adds a stub enum for a type Cecil failed to resolve; false when it cannot help
    public bool AddStub(TypeReference tr)
    {
        if (tr.Scope is not AssemblyNameReference an) return false;
        var asm = Resolve(an);
        var mod = asm.MainModule;
        var chain = new List<TypeReference>();
        for (var t = tr; t != null; t = t.DeclaringType) chain.Insert(0, t);
        TypeDefinition? parent = null;
        foreach (var part in chain)
        {
            var existing = parent == null ? mod.GetType(part.Namespace, part.Name) : parent.NestedTypes.FirstOrDefault(n => n.Name == part.Name);
            if (existing == null)
            {
                existing = new TypeDefinition(parent == null ? part.Namespace : "", part.Name, TypeAttributes.Public | TypeAttributes.Sealed | (parent == null ? 0 : TypeAttributes.NestedPublic), null);
                if (parent == null) mod.Types.Add(existing); else parent.NestedTypes.Add(existing);
            }
            parent = existing;
        }
        Retries++;
        var enumDef = parent!;
        if (enumDef.Fields.Count > 0) return false;
        var sys = new AssemblyNameReference("netstandard", new Version(2, 1, 0, 0));
        TypeReference Sys(string n, bool value = true) => new TypeReference("System", n, mod, sys, value);
        enumDef.BaseType = Sys("Enum");
        var ct = constantTypes.TryGetValue(tr.FullName, out var c) ? c : typeof(int);
        TypeReference under = Sys(ct == typeof(byte) ? "Byte" : ct == typeof(sbyte) ? "SByte" : ct == typeof(short) ? "Int16" : ct == typeof(ushort) ? "UInt16"
            : ct == typeof(uint) ? "UInt32" : ct == typeof(long) ? "Int64" : ct == typeof(ulong) ? "UInt64" : "Int32");
        enumDef.Fields.Add(new FieldDefinition("value__", FieldAttributes.Public | FieldAttributes.SpecialName | FieldAttributes.RTSpecialName, under));
        return true;
    }
}

/// in-memory entry point for the patcher: bytes in, bytes out, verified. used by the cli
/// and by the browser (wasm) host, so it does no file or console i/o of its own.
public static class PatchApi
{
    public const string Version = "v1.0";

    // the patcher keeps working refs in statics and the log goes through Console, so
    // one patch at a time
    private static readonly object Gate = new();

    /// patches a Silksong Assembly-CSharp.dll and verifies the result before returning it.
    /// refs resolves unity assemblies for VerifyMode.Full and may be null for
    /// VerifyMode.ReferenceFree (null in Full mode is an error).
    public static PatchResult Patch(byte[] input, VerifyMode mode, IAssemblyResolver? refs)
    {
        lock (Gate)
        {
            var log = new StringWriter();
            var previous = Console.Out;
            Console.SetOut(log);
            try
            {
                return PatchCore(input, mode, refs, log);
            }
            catch (Exception ex)
            {
                log.WriteLine($"[!] {ex.GetType().Name}: {ex.Message}");
                log.WriteLine(ex.StackTrace);
                return Fail("error", mode, log);
            }
            finally
            {
                Console.SetOut(previous);
            }
        }
    }

    private static PatchResult PatchCore(byte[] input, VerifyMode mode, IAssemblyResolver? refs, StringWriter log)
    {
        if (input == null || input.Length == 0)
        {
            log.WriteLine("[!] input is empty");
            return Fail("error", mode, log);
        }
        if (mode == VerifyMode.Full && refs == null)
        {
            log.WriteLine("[!] VerifyMode.Full needs an assembly resolver for the unity references");
            return Fail("error", mode, log);
        }

        log.WriteLine($"[+] patching assembly ({input.Length} bytes), verify mode: {mode}");
        var clock = Stopwatch.StartNew();
        byte[] patched;

        // the patcher itself resolves nothing, so it never gets the host's resolver
        AssemblyDefinition asm;
        var stub = new StubResolver();
        try
        {
            asm = AssemblyDefinition.ReadAssembly(new MemoryStream(input),
                new ReaderParameters { AssemblyResolver = stub });
        }
        catch (Exception ex)
        {
            log.WriteLine($"[!] input is not a readable .net assembly ({ex.GetType().Name}: {ex.Message})");
            return Fail("not_silksong", mode, log);
        }

        using (asm)
        {
            var types = new HashSet<string>(asm.MainModule.Types.Select(t => t.Name));
            if (types.Contains(Program.HelperTypeName))
            {
                log.WriteLine($"[!] this assembly is already patched (it contains {Program.HelperTypeName}). patch the original Assembly-CSharp.dll");
                return Fail("already_patched", mode, log);
            }
            if (!types.Contains("ForceCameraAspect") || !types.Contains("CameraController") || !types.Contains("HeroController"))
            {
                log.WriteLine("[!] this is not Silksong's Assembly-CSharp.dll (no ForceCameraAspect / CameraController type)");
                return Fail("not_silksong", mode, log);
            }

            patched = Program.PatchAssembly(asm, stub);
        }
        long patchMs = clock.ElapsedMilliseconds;

        // gate on the verifier, run on the bytes we are about to hand out
        clock.Restart();
        var verifyLog = new StringWriter();
        var checks = new Checks(verifyLog);
        using (var ms = new MemoryStream(patched))
        using (var module = ModuleDefinition.ReadModule(ms,
                   new ReaderParameters { AssemblyResolver = refs ?? new NullAssemblyResolver() }))
        {
            VerifyRunner.Run(checks, module, mode, input);
        }
        checks.Summarize();
        long verifyMs = clock.ElapsedMilliseconds;

        bool ok = checks.Failed == 0;
        if (!ok) log.WriteLine($"[!] verification failed ({checks.Failed} check(s)); no output");
        return new PatchResult
        {
            Output = ok ? patched : null,
            Reason = ok ? "ok" : "verify_failed",
            Log = log.ToString(),
            VerifyLog = verifyLog.ToString(),
            Passed = checks.Passed,
            Failed = checks.Failed,
            Skipped = checks.Skipped,
            Failures = checks.Failures.ToArray(),
            Mode = mode,
            PatchMillis = patchMs,
            VerifyMillis = verifyMs,
            Sha256 = ok ? Convert.ToHexString(SHA256.HashData(patched)).ToLowerInvariant() : null,
        };
    }

    private static PatchResult Fail(string reason, VerifyMode mode, StringWriter log) =>
        new() { Reason = reason, Log = log.ToString(), Mode = mode };
}
