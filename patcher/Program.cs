using System.Security.Cryptography;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Silksong43.Verify;

/// Silksong 4:3 patcher. Rewrites Assembly-CSharp.dll with Mono.Cecil. Everything is built by hand
/// and scoped to the module's own assembly references (netstandard, UnityEngine.*), so the same code
/// runs in the browser and nothing resolves against the patcher's own .NET.
///
/// Edits (see CLAUDE.md for the reasoning):
///   ForceCameraAspect.AutoScaleViewportShared  aspect clamp floor 1.6 -> 4/3: no letterbox at 4:3
///   ForceCameraAspect.AutoScaleViewport        FOV scaled by tan, not linearly (exact height multiplier)
///   CameraController.LateUpdate                after the scene-bounds clamp, re-clamp y so a taller view
///                                              never shows more than a 16:9 view would past room/lock edges
///   new CameraController.Fix43Y, new Nova43Camera.{ScaleFov,AdjustY}
public static class Program
{
    public const string HelperTypeName = "Nova43Camera";
    public const string FixMethodName = "Fix43Y";
    public const float TallAspect = 1.3333334f; // 4/3, the new floor of the viewport aspect clamp

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.WriteLine("usage: patcher <Assembly-CSharp.dll> [managedDir] [outputDll]\n" +
                              "  with managedDir the full verifier runs (resolves every member against the real Unity dlls)\n" +
                              "  without it a reference-free subset runs. output is written only if verification passes.\n" +
                              "  exit codes: 0 ok, 1 verify failed/error, 3 already patched, 4 not Silksong's dll");
            return 0;
        }
        string input = args[0];
        string? managed = args.Length > 1 ? args[1] : null;
        string output = args.Length > 2 ? args[2] : Path.Combine(Path.GetDirectoryName(Path.GetFullPath(input))!, "Assembly-CSharp-43.dll");
        var bytes = File.ReadAllBytes(input);
        Console.WriteLine($"input  sha256 {Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}");
        var mode = managed != null ? VerifyMode.Full : VerifyMode.ReferenceFree;
        var resolver = managed != null ? new DefaultAssemblyResolver() : null;
        resolver?.AddSearchDirectory(managed!);
        var r = PatchApi.Patch(bytes, mode, resolver);
        Console.Write(r.Log);
        if (!string.IsNullOrEmpty(r.VerifyLog)) Console.Write(r.VerifyLog);
        Console.WriteLine(r.Summary);
        switch (r.Reason)
        {
            case "ok":
                File.WriteAllBytes(output, r.Output!);
                Console.WriteLine($"output sha256 {r.Sha256}\nwrote {output}");
                return 0;
            case "already_patched": return 3;
            case "not_silksong": return 4;
            default: return 1;
        }
    }

    // ---- patch ----

    public static byte[] PatchAssembly(AssemblyDefinition asm, StubResolver stub)
    {
        var m = asm.MainModule;
        var fca = m.GetType("ForceCameraAspect") ?? throw new Exception("ForceCameraAspect missing");
        var cc = m.GetType("CameraController") ?? throw new Exception("CameraController missing");
        var ns = m.AssemblyReferences.FirstOrDefault(a => a.Name == "netstandard") ?? throw new Exception("netstandard reference missing");

        var helper = BuildHelperType(m, ns, cc, fca);
        var adjustY = helper.Methods.First(x => x.Name == "AdjustY");
        var scaleFov = helper.Methods.First(x => x.Name == "ScaleFov");

        PatchAspectFloor(fca);
        PatchFov(fca, m, scaleFov);
        var fix = BuildFix43Y(m, cc, fca, adjustY);
        PatchLateUpdate(cc, fix);

        stub.Scan(m);
        for (int attempt = 0; ; attempt++)
        {
            var ms = new MemoryStream();
            try
            {
                asm.Write(ms);
                Console.WriteLine($"[+] wrote assembly ({stub.Retries} stub enum retries)");
                return ms.ToArray();
            }
            catch (ResolutionException e) when (attempt < 200 && Stub(stub, e))
            {
                continue;
            }
        }
    }

    static bool Stub(StubResolver stub, ResolutionException e)
    {
        var tr = e.Member as TypeReference;
        bool ok = false;
        try { ok = tr != null && stub.AddStub(tr); } catch (Exception x) { Console.Error.WriteLine(x); }
        if (!ok) Console.WriteLine($"[!] cannot stub {e.Member?.GetType().Name} {e.Member?.FullName}");
        return ok;
    }

    static MethodDefinition Get(TypeDefinition t, string name) =>
        t.Methods.FirstOrDefault(x => x.Name == name) ?? throw new Exception($"{t.Name}.{name} missing");

    // 1.6 -> 4/3: the viewport aspect is clamped to [1.6, 2.39]; with the floor at 4/3 a 4:3 screen is
    // not letterboxed and the height multiplier becomes (16/9)/(4/3)
    static void PatchAspectFloor(TypeDefinition fca)
    {
        var shared = Get(fca, "AutoScaleViewportShared");
        var hits = shared.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 1.6f).ToList();
        if (hits.Count != 1) throw new Exception($"AutoScaleViewportShared: expected one ldc.r4 1.6, found {hits.Count}");
        hits[0].Operand = TallAspect;
    }

    // fov * heightMult scales the vertical field of view linearly, but the visible height scales with
    // tan(fov/2). for 16:9 -> 4:3 that is 1.41x instead of 1.33x. Scale by tan so the view is exactly
    // heightMult taller, which is what the camera bounds below assume.
    static void PatchFov(TypeDefinition fca, ModuleDefinition m, MethodDefinition scaleFov)
    {
        var asv = Get(fca, "AutoScaleViewport");
        var ins = asv.Body.Instructions;
        var fovField = ins.First(i => i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Name == "fieldOfView");
        // expected: ... add, ldloc.1, mul, stloc.2, ldarg.0, ldfld tk2dCam, callvirt get_CameraSettings, ldloc.2, stfld
        var idx = ins.IndexOf(fovField);
        Instruction? mulIns = null;
        for (int i = idx; i >= 0 && i > idx - 10; i--)
            if (ins[i].OpCode == OpCodes.Mul) { mulIns = ins[i]; break; }
        if (mulIns == null || mulIns.Previous.OpCode != OpCodes.Ldloc_1) throw new Exception("AutoScaleViewport: fov multiply not found");
        mulIns.OpCode = OpCodes.Call;
        mulIns.Operand = scaleFov;
    }

    static TypeReference Single(ModuleDefinition m) => m.TypeSystem.Single;

    static TypeDefinition BuildHelperType(ModuleDefinition m, AssemblyNameReference ns, TypeDefinition cc, TypeDefinition fca)
    {
        if (m.GetType(HelperTypeName) != null) throw new Exception("already patched");
        var t = new TypeDefinition("", HelperTypeName,
            TypeAttributes.Public | TypeAttributes.Abstract | TypeAttributes.Sealed | TypeAttributes.BeforeFieldInit | TypeAttributes.AutoClass | TypeAttributes.AnsiClass,
            m.TypeSystem.Object);
        m.Types.Add(t);

        var mathType = new TypeReference("System", "Math", m, ns);
        var dbl = m.TypeSystem.Double;
        MethodReference MathFn(string name)
        {
            var r = new MethodReference(name, dbl, mathType);
            r.Parameters.Add(new ParameterDefinition(dbl));
            return r;
        }

        // static float ScaleFov(float fov, float mult)  =  2 * atan(tan(fov/2 deg) * mult) in degrees
        var sf = new MethodDefinition("ScaleFov", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, Single(m));
        sf.Parameters.Add(new ParameterDefinition("fov", ParameterAttributes.None, Single(m)));
        sf.Parameters.Add(new ParameterDefinition("mult", ParameterAttributes.None, Single(m)));
        {
            var il = sf.Body.GetILProcessor();
            var ret = il.Create(OpCodes.Ldarg_0);
            // if (mult <= 0 || fov <= 0) return fov * mult
            var guard = il.Create(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldc_R4, 0f); il.Emit(OpCodes.Ble_Un, guard);
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldc_R4, 0f); il.Emit(OpCodes.Bgt_Un, ret);
            il.Append(guard); il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Ret);
            // 2 * atan(tan(fov * (pi/360)) * mult) * (180/pi)
            il.Append(ret);
            il.Emit(OpCodes.Conv_R8); il.Emit(OpCodes.Ldc_R8, Math.PI / 360.0); il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Call, MathFn("Tan"));
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Conv_R8); il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Call, MathFn("Atan"));
            il.Emit(OpCodes.Ldc_R8, 360.0 / Math.PI); il.Emit(OpCodes.Mul);
            il.Emit(OpCodes.Conv_R4); il.Emit(OpCodes.Ret);
        }
        t.Methods.Add(sf);

        // static float AdjustY(float y, float lo, float hi, float mult)
        // lo/hi are the camera-centre limits the game computed for a 16:9 view (half height 8.3). the
        // taller view needs d = 8.3 * (mult - 1) more room each side. if the window is too short for the
        // taller view the camera centres on it.
        var ay = new MethodDefinition("AdjustY", MethodAttributes.Public | MethodAttributes.Static | MethodAttributes.HideBySig, Single(m));
        foreach (var n in new[] { "y", "lo", "hi", "mult" }) ay.Parameters.Add(new ParameterDefinition(n, ParameterAttributes.None, Single(m)));
        ay.Body.InitLocals = true;
        var vd = new VariableDefinition(Single(m)); ay.Body.Variables.Add(vd);   // d
        var vlo = new VariableDefinition(Single(m)); ay.Body.Variables.Add(vlo); // lo + d
        var vhi = new VariableDefinition(Single(m)); ay.Body.Variables.Add(vhi); // hi - d
        {
            var il = ay.Body.GetILProcessor();
            var retY = il.Create(OpCodes.Ldarg_0);
            var centre = il.Create(OpCodes.Ldarg_1);
            // if (!(mult > 1)) return y;   (also catches NaN and the 0 default before the camera is set up)
            il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldc_R4, 1f); il.Emit(OpCodes.Ble_Un, retY);
            // d = 8.3 * (mult - 1)
            il.Emit(OpCodes.Ldc_R4, 8.3f); il.Emit(OpCodes.Ldarg_3); il.Emit(OpCodes.Ldc_R4, 1f); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Stloc, vd);
            il.Emit(OpCodes.Ldarg_1); il.Emit(OpCodes.Ldloc, vd); il.Emit(OpCodes.Add); il.Emit(OpCodes.Stloc, vlo);
            il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Ldloc, vd); il.Emit(OpCodes.Sub); il.Emit(OpCodes.Stloc, vhi);
            // if (lo2 > hi2) return (lo + hi) / 2
            il.Emit(OpCodes.Ldloc, vlo); il.Emit(OpCodes.Ldloc, vhi); il.Emit(OpCodes.Bgt_Un, centre);
            var notLow = il.Create(OpCodes.Ldarg_0);
            // if (y < lo2) return lo2;
            il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldloc, vlo); il.Emit(OpCodes.Bge_Un, notLow);
            il.Emit(OpCodes.Ldloc, vlo); il.Emit(OpCodes.Ret);
            // if (y > hi2) return hi2;
            il.Append(notLow); il.Emit(OpCodes.Ldloc, vhi); il.Emit(OpCodes.Ble_Un, retY);
            il.Emit(OpCodes.Ldloc, vhi); il.Emit(OpCodes.Ret);
            il.Append(retY); il.Emit(OpCodes.Ret);
            il.Append(centre); il.Emit(OpCodes.Ldarg_2); il.Emit(OpCodes.Add); il.Emit(OpCodes.Ldc_R4, 0.5f); il.Emit(OpCodes.Mul); il.Emit(OpCodes.Ret);
        }
        t.Methods.Add(ay);
        return t;
    }

    // private float Fix43Y(float y): picks the 16:9 centre limits that currently apply (lock area or scene)
    // and hands them to Nova43Camera.AdjustY with the live height multiplier
    static MethodDefinition BuildFix43Y(ModuleDefinition m, TypeDefinition cc, TypeDefinition fca, MethodDefinition adjustY)
    {
        var mode = cc.Fields.First(f => f.Name == "mode");
        var yLockMin = cc.Fields.First(f => f.Name == "yLockMin");
        var yLockMax = cc.Fields.First(f => f.Name == "yLockMax");
        var yLimit = cc.Fields.First(f => f.Name == "yLimit");
        if (mode.FieldType.Name != "CameraMode") throw new Exception("CameraController.mode is not CameraMode");
        var lockedVal = ((TypeDefinition)mode.FieldType).Fields.First(f => f.Name == "LOCKED");
        int locked = Convert.ToInt32(lockedVal.Constant);
        var getMult = fca.Methods.First(x => x.Name == "get_CurrentMainCamHeightMult");

        var fix = new MethodDefinition(FixMethodName, MethodAttributes.Private | MethodAttributes.HideBySig, Single(m));
        fix.Parameters.Add(new ParameterDefinition("y", ParameterAttributes.None, Single(m)));
        var lo = new VariableDefinition(Single(m)); fix.Body.Variables.Add(lo);
        var hi = new VariableDefinition(Single(m)); fix.Body.Variables.Add(hi);
        fix.Body.InitLocals = true;
        var il = fix.Body.GetILProcessor();
        var elseL = il.Create(OpCodes.Ldc_R4, 8.3f);
        var call = il.Create(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, mode); il.Emit(OpCodes.Ldc_I4, locked); il.Emit(OpCodes.Bne_Un, elseL);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, yLockMin); il.Emit(OpCodes.Stloc, lo);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, yLockMax); il.Emit(OpCodes.Stloc, hi);
        il.Emit(OpCodes.Br, call);
        il.Append(elseL); il.Emit(OpCodes.Stloc, lo);
        il.Emit(OpCodes.Ldarg_0); il.Emit(OpCodes.Ldfld, yLimit); il.Emit(OpCodes.Stloc, hi);
        il.Append(call); il.Emit(OpCodes.Ldloc, lo); il.Emit(OpCodes.Ldloc, hi);
        il.Emit(OpCodes.Call, getMult);
        il.Emit(OpCodes.Call, adjustY);
        il.Emit(OpCodes.Ret);
        cc.Methods.Add(fix);
        return fix;
    }

    // after the game's own scene-bounds clamp (the block guarded by !AllowExitingSceneBounds) add
    //   position.y = Fix43Y(position.y);
    // the clamp's "in bounds" branch and fall-through both land on the SetPosition2D call, so the new
    // block goes in front of it, and only the AllowExitingSceneBounds skip keeps jumping over it.
    static void PatchLateUpdate(TypeDefinition cc, MethodDefinition fix)
    {
        var lu = Get(cc, "LateUpdate");
        var body = lu.Body;
        var ins = body.Instructions;
        var allow = ins.First(i => i.OpCode == OpCodes.Call && ((MethodReference)i.Operand).Name == "get_AllowExitingSceneBounds");
        var skip = allow.Next;
        if (skip.OpCode != OpCodes.Brtrue_S && skip.OpCode != OpCodes.Brtrue) throw new Exception("LateUpdate: unexpected shape after AllowExitingSceneBounds");
        var join = (Instruction)skip.Operand; // ldarg.0; call get_transform; ldloc position; ... SetPosition2D
        if (join.OpCode != OpCodes.Ldarg_0) throw new Exception("LateUpdate: unexpected join point");
        // the Vector3 local named position is the one the clamp stores through
        var pos = ((VariableReference)ins.First(i => (i.OpCode == OpCodes.Ldloca_S || i.OpCode == OpCodes.Ldloca) && i.Next.OpCode == OpCodes.Ldc_R4 && (float)i.Next.Operand == 8.3f).Operand);
        var yField = ins.First(i => i.OpCode == OpCodes.Stfld && ((FieldReference)i.Operand).Name == "y" && ((FieldReference)i.Operand).DeclaringType.Name == "Vector3").Operand as FieldReference;
        // retarget branches that reach the join point from inside the clamp block to the new code;
        // the AllowExitingSceneBounds skip keeps its target
        var il = body.GetILProcessor();
        var first = il.Create(OpCodes.Ldloca_S, (VariableDefinition)pos);
        var newCode = new List<Instruction>
        {
            first,
            il.Create(OpCodes.Ldarg_0),
            il.Create(OpCodes.Ldloca_S, (VariableDefinition)pos),
            il.Create(OpCodes.Ldfld, yField!),
            il.Create(OpCodes.Call, fix),
            il.Create(OpCodes.Stfld, yField!),
        };
        // ldloca position; ldarg.0; ldloca position; ldfld y; call Fix43Y; stfld y  => leaves position.y = Fix43Y(position.y)
        int retargeted = 0;
        foreach (var i in ins)
        {
            if (i == skip) continue;
            if (i.Operand == join && (i.OpCode.FlowControl == FlowControl.Cond_Branch || i.OpCode.FlowControl == FlowControl.Branch))
            {
                i.Operand = first; retargeted++;
            }
        }
        if (retargeted == 0) throw new Exception("LateUpdate: no branch into the join point");
        foreach (var n in newCode) il.InsertBefore(join, n);
    }
}
