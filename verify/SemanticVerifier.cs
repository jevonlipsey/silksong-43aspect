using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Silksong43.Verify;

/// semantic verifier: diffs the patched module against the original and proves the patch changed
/// exactly what it claims and nothing else.
public sealed class SemanticVerifier
{
    const string Helper = "Nova43Camera";
    readonly Checks checks;
    readonly ModuleDefinition orig, patched;
    readonly Diff diff;

    public SemanticVerifier(Checks checks, ModuleDefinition orig, ModuleDefinition patched, Diff diff)
    {
        this.checks = checks; this.orig = orig; this.patched = patched; this.diff = diff;
    }

    static MethodDefinition? M(ModuleDefinition mod, string type, string name) =>
        mod.GetType(type)?.Methods.FirstOrDefault(m => m.Name == name);

    public void Run()
    {
        checks.Section("whole-module diff");
        var oref = orig.AssemblyReferences.Select(r => r.FullName).OrderBy(x => x).ToList();
        var pref = patched.AssemblyReferences.Select(r => r.FullName).OrderBy(x => x).ToList();
        checks.Check(oref.SequenceEqual(pref), $"assembly references unchanged ({oref.Count})");
        checks.Check(diff.AddedTypes.SetEquals(new[] { Helper }), $"the only new type is {Helper} (added: {string.Join(",", diff.AddedTypes)})");
        checks.Check(diff.RemovedTypes.Count == 0, "no type removed");
        checks.Check(diff.FieldChanges == 0, "no field added or removed on any existing type");
        var expected = new[]
        {
            "System.Single ForceCameraAspect::AutoScaleViewportShared(System.Single,UnityEngine.Rect&,UnityEngine.Rect&,System.Single&)",
            "System.Single ForceCameraAspect::AutoScaleViewport()",
            "System.Void CameraController::LateUpdate()",
            "System.Single CameraController::Fix43Y(System.Single)",
            $"System.Single {Helper}::ScaleFov(System.Single,System.Single)",
            $"System.Single {Helper}::AdjustY(System.Single,System.Single,System.Single,System.Single)",
        };
        var extra = diff.Touched.Except(expected).ToList();
        var missing = expected.Except(diff.Touched).ToList();
        checks.Check(extra.Count == 0 && missing.Count == 0,
            extra.Count == 0 && missing.Count == 0
                ? $"exactly {expected.Length} methods differ from the original, out of {diff.MethodsCompared}"
                : $"unexpected: [{string.Join("; ", extra.Take(5))}] missing: [{string.Join("; ", missing)}]");

        checks.Section("aspect floor (ForceCameraAspect.AutoScaleViewportShared)");
        var so = M(orig, "ForceCameraAspect", "AutoScaleViewportShared");
        var sp = M(patched, "ForceCameraAspect", "AutoScaleViewportShared");
        if (so == null || sp == null) checks.Fail("AutoScaleViewportShared missing");
        else
        {
            var a = Diff.Sigs(so); var b = Diff.Sigs(sp);
            checks.Check(a.Count == b.Count, "instruction count unchanged");
            var d = Enumerable.Range(0, Math.Min(a.Count, b.Count)).Where(i => a[i] != b[i]).ToList();
            checks.Check(d.Count == 1, $"exactly one instruction differs ({d.Count})");
            if (d.Count == 1)
            {
                var oi = so.Body.Instructions[d[0]]; var pi = sp.Body.Instructions[d[0]];
                checks.Check(oi.OpCode == OpCodes.Ldc_R4 && (float)oi.Operand == 1.6f, "original: ldc.r4 1.6 (the 16:10 floor)");
                checks.Check(pi.OpCode == OpCodes.Ldc_R4 && (float)pi.Operand == 1.3333334f, "patched: ldc.r4 4/3");
                var next = sp.Body.Instructions[d[0] + 1];
                checks.Check(next.OpCode == OpCodes.Ldc_R4 && (float)next.Operand == 2.3916667f, "ceiling 2.3916667 untouched, same MinMaxFloat ctor call");
            }
        }

        checks.Section("fov (ForceCameraAspect.AutoScaleViewport)");
        var fo = M(orig, "ForceCameraAspect", "AutoScaleViewport");
        var fp = M(patched, "ForceCameraAspect", "AutoScaleViewport");
        if (fo == null || fp == null) checks.Fail("AutoScaleViewport missing");
        else
        {
            var a = Diff.Sigs(fo); var b = Diff.Sigs(fp);
            checks.Check(a.Count == b.Count, "instruction count unchanged");
            var d = Enumerable.Range(0, Math.Min(a.Count, b.Count)).Where(i => a[i] != b[i]).ToList();
            checks.Check(d.Count == 1, $"exactly one instruction differs ({d.Count})");
            if (d.Count == 1)
            {
                var ins = fp.Body.Instructions;
                var pi = ins[d[0]];
                checks.Check(fo.Body.Instructions[d[0]].OpCode == OpCodes.Mul, "original: mul (fov * heightMult)");
                checks.Check(pi.OpCode == OpCodes.Call && pi.Operand is MethodReference mr && mr.Name == "ScaleFov" && mr.DeclaringType.Name == Helper, $"patched: call {Helper}.ScaleFov");
                checks.Check(ins[d[0] - 1].OpCode == OpCodes.Ldloc_1 && ins[d[0] + 1].OpCode == OpCodes.Stloc_2, "fed by ldloc.1 (heightMult), stored to the fov local");
            }
        }

        checks.Section("camera bounds (CameraController.LateUpdate / Fix43Y)");
        var lo = M(orig, "CameraController", "LateUpdate");
        var lp = M(patched, "CameraController", "LateUpdate");
        var fix = M(patched, "CameraController", "Fix43Y");
        if (lo == null || lp == null || fix == null) checks.Fail("LateUpdate / Fix43Y missing");
        else
        {
            var a = Diff.Sigs(lo); var b = Diff.Sigs(lp);
            checks.Check(b.Count == a.Count + 8, $"LateUpdate grew by exactly 8 instructions ({a.Count} -> {b.Count})");
            // find the inserted block: the 8 consecutive patched instructions that call Fix43Y
            int call = b.FindIndex(x => x.StartsWith("Call") && x.Contains("CameraController::Fix43Y"));
            checks.Check(call >= 6, "Fix43Y is called from LateUpdate");
            if (call >= 6 && b.Count == a.Count + 8)
            {
                int start = call - 6;
                var shape = new[] { "Ldarg_0", "Ldflda", "Ldarg_0", "Ldarg_0", "Ldflda", "Ldfld", "Call", "Stfld" };
                bool shapeOk = Enumerable.Range(0, 8).All(k => b[start + k].StartsWith(shape[k]));
                checks.Check(shapeOk, "inserted block is destination.y = Fix43Y(destination.y)");
                checks.Check(b[start + 5].Contains("Vector3::y") && b[start + 7].Contains("Vector3::y"), "reads and writes Vector3.y");
                // without the block, instructions are identical in order and every branch target maps
                // to the same original target (a target equal to the block start maps to the join point)
                var stripped = b.Take(start).Concat(b.Skip(start + 8)).ToList();
                int Map(int target) => target > start ? target - 8 : target; // target==start -> start == original join index
                bool same = stripped.Count == a.Count;
                int retargets = 0, bad = 0;
                for (int i = 0; same && i < a.Count; i++)
                {
                    var x = a[i]; var y = stripped[i];
                    int at = x.IndexOf('@');
                    if (at < 0 || x.Contains(',')) { if (x != y) { bad++; } continue; }
                    // branch: compare opcode and target after mapping
                    int oldT = int.Parse(x[(at + 1)..]);
                    var yat = y.IndexOf('@');
                    if (yat < 0 || y[..yat] != x[..at]) { bad++; continue; }
                    int newT = int.Parse(y[(yat + 1)..]);
                    if (Map(newT) != oldT) bad++;
                    else if (newT == start && oldT == start) retargets++;
                }
                checks.Check(same && bad == 0, $"all other instructions and branch targets identical to the original ({bad} mismatches)");
                checks.Check(retargets >= 1, $"{retargets} branch(es) into the join point now run the new block");
            }
            // the original clamp literals are still there (we did not move the 16:9 bounds)
            checks.Check(lp.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 8.3f) == 2 &&
                         lp.Body.Instructions.Count(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 14.6f) == 2,
                "LateUpdate keeps the game's own 8.3 / 14.6 clamps");

            var cc = patched.GetType("CameraController")!;
            var fieldsUsed = fix.Body.Instructions.Where(i => i.OpCode == OpCodes.Ldfld).Select(i => ((FieldReference)i.Operand).Name).Distinct().OrderBy(x => x).ToList();
            checks.Check(fieldsUsed.SequenceEqual(new[] { "mode", "yLimit", "yLockMax", "yLockMin" }), $"Fix43Y reads only mode, yLockMin, yLockMax, yLimit ({string.Join(",", fieldsUsed)})");
            var modeEnum = (TypeDefinition)cc.Fields.First(f => f.Name == "mode").FieldType;
            int locked = Convert.ToInt32(modeEnum.Fields.First(f => f.Name == "LOCKED").Constant);
            var cmp = fix.Body.Instructions.First(i => i.OpCode == OpCodes.Ldc_I4);
            checks.Check(Convert.ToInt32(cmp.Operand) == locked, $"Fix43Y compares mode with CameraMode.LOCKED ({locked})");
            var calls = fix.Body.Instructions.Where(i => i.OpCode == OpCodes.Call).Select(i => ((MethodReference)i.Operand).Name).ToList();
            checks.Check(calls.SequenceEqual(new[] { "get_CurrentMainCamHeightMult", "AdjustY" }), $"Fix43Y calls ForceCameraAspect.CurrentMainCamHeightMult then AdjustY ({string.Join(",", calls)})");
            checks.Check(fix.Body.Instructions.Any(i => i.OpCode == OpCodes.Ldc_R4 && (float)i.Operand == 8.3f), "Fix43Y falls back to the scene lower bound 8.3");
        }

        checks.Section($"helper type {Helper}");
        var h = patched.GetType(Helper);
        if (h == null) { checks.Fail("helper type missing"); return; }
        checks.Check(h.IsPublic && h.IsAbstract && h.IsSealed, "public static class");
        checks.Check(h.Fields.Count == 0 && h.Methods.All(m => m.IsStatic && m.IsPublic), "no fields; all methods public static");
        checks.Check(h.Methods.Select(m => m.Name).OrderBy(x => x).SequenceEqual(new[] { "AdjustY", "ScaleFov" }), "methods: AdjustY, ScaleFov");
        var hostCalls = h.Methods.SelectMany(m => m.Body.Instructions).Where(i => i.Operand is MethodReference).Select(i => (MethodReference)i.Operand).ToList();
        checks.Check(hostCalls.All(c => c.DeclaringType.FullName == "System.Math" && c.DeclaringType.Scope.Name == "netstandard" && (c.Name == "Tan" || c.Name == "Atan")),
            $"helper only calls netstandard System.Math.Tan/Atan ({hostCalls.Count} calls)");
        checks.Check(h.Methods.SelectMany(m => m.Body.Instructions).All(i => i.Operand is not FieldReference), "helper touches no fields");
    }
}
