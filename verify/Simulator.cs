using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Silksong43.Verify;

/// numeric verifier. a small IL interpreter runs the injected methods (Nova43Camera.ScaleFov /
/// AdjustY and CameraController.Fix43Y) exactly as emitted, and compares them with an independent
/// C# model of the intended behaviour over a grid, then checks the property the patch exists for:
/// the visible vertical window never exceeds what a 16:9 camera would have shown past a room or
/// lock-area edge, unless the room is shorter than the view (then it is centred).
public sealed class Simulator
{
    readonly Checks checks;
    readonly ModuleDefinition module;
    const float HalfH = 8.3f;

    public Simulator(Checks checks, ModuleDefinition module) { this.checks = checks; this.module = module; }

    // ---- interpreter ----

    sealed class Env
    {
        public Dictionary<string, object> Fields = new();
        public float HeightMult;
    }

    static double D(object o) => o switch { float f => f, double d => d, int i => i, long l => l, _ => throw new InvalidOperationException("not numeric: " + o) };

    object Run(MethodDefinition m, object?[] args, Env env, int depth = 0)
    {
        if (depth > 8) throw new InvalidOperationException("call depth");
        var ins = m.Body.Instructions;
        var idx = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) idx[ins[i]] = i;
        var st = new Stack<object>();
        var loc = new object[m.Body.Variables.Count];
        for (int i = 0; i < loc.Length; i++) loc[i] = 0f;
        int pc = 0, steps = 0;
        object Arg(int n) => args[n]!;
        while (true)
        {
            if (++steps > 10000) throw new InvalidOperationException("runaway");
            var i = ins[pc];
            int next = pc + 1;
            switch (i.OpCode.Code)
            {
                case Code.Ldarg_0: st.Push(Arg(0)); break;
                case Code.Ldarg_1: st.Push(Arg(1)); break;
                case Code.Ldarg_2: st.Push(Arg(2)); break;
                case Code.Ldarg_3: st.Push(Arg(3)); break;
                case Code.Ldloc: case Code.Ldloc_S: st.Push(loc[((VariableDefinition)i.Operand).Index]); break;
                case Code.Stloc: case Code.Stloc_S: loc[((VariableDefinition)i.Operand).Index] = st.Pop(); break;
                case Code.Ldc_R4: st.Push((float)i.Operand); break;
                case Code.Ldc_R8: st.Push((double)i.Operand); break;
                case Code.Ldc_I4: st.Push((int)i.Operand); break;
                case Code.Ldfld: { st.Pop(); st.Push(env.Fields[((FieldReference)i.Operand).Name]); break; }
                case Code.Add: case Code.Sub: case Code.Mul: case Code.Div:
                {
                    var b = st.Pop(); var a = st.Pop();
                    bool dbl = a is double || b is double;
                    double x = D(a), y = D(b);
                    double r = i.OpCode.Code switch { Code.Add => x + y, Code.Sub => x - y, Code.Mul => x * y, _ => x / y };
                    st.Push(dbl ? r : (float)r);
                    break;
                }
                case Code.Conv_R8: st.Push(D(st.Pop())); break;
                case Code.Conv_R4: st.Push((float)D(st.Pop())); break;
                case Code.Br: case Code.Br_S: next = idx[(Instruction)i.Operand]; break;
                case Code.Ble_Un: case Code.Bge_Un: case Code.Bgt_Un: case Code.Blt_Un: case Code.Bne_Un: case Code.Bne_Un_S:
                case Code.Ble_Un_S: case Code.Bge_Un_S: case Code.Bgt_Un_S: case Code.Blt_Un_S:
                {
                    var b = st.Pop(); var a = st.Pop();
                    double x = D(a), y = D(b);
                    bool take = i.OpCode.Code switch
                    {
                        Code.Ble_Un or Code.Ble_Un_S => !(x > y),
                        Code.Bge_Un or Code.Bge_Un_S => !(x < y),
                        Code.Bgt_Un or Code.Bgt_Un_S => !(x <= y),
                        Code.Blt_Un or Code.Blt_Un_S => !(x >= y),
                        _ => x != y || double.IsNaN(x) || double.IsNaN(y),
                    };
                    if (take) next = idx[(Instruction)i.Operand];
                    break;
                }
                case Code.Call:
                {
                    var mr = (MethodReference)i.Operand;
                    if (mr.DeclaringType.FullName == "System.Math")
                    {
                        var v = D(st.Pop());
                        st.Push(mr.Name == "Tan" ? Math.Tan(v) : mr.Name == "Atan" ? Math.Atan(v) : throw new InvalidOperationException(mr.FullName));
                    }
                    else if (mr.Name == "get_CurrentMainCamHeightMult") st.Push(env.HeightMult);
                    else
                    {
                        var def = mr.Resolve();
                        var cargs = new object?[mr.Parameters.Count + (mr.HasThis ? 1 : 0)];
                        for (int k = cargs.Length - 1; k >= 0; k--) cargs[k] = st.Pop();
                        st.Push(Run(def, cargs, env, depth + 1));
                    }
                    break;
                }
                case Code.Ret: return m.ReturnType.MetadataType == MetadataType.Void ? 0 : st.Pop();
                default: throw new InvalidOperationException($"simulator: unsupported opcode {i.OpCode} in {m.Name}");
            }
            pc = next;
        }
    }

    // ---- independent models ----

    static float ModelAdjustY(float y, float lo, float hi, float mult)
    {
        if (!(mult > 1f)) return y;
        float d = HalfH * (mult - 1f);
        float lo2 = lo + d, hi2 = hi - d;
        if (lo2 > hi2) return (lo + hi) * 0.5f;
        return Math.Min(Math.Max(y, lo2), hi2);
    }

    static float ModelFov(float fov, float mult)
    {
        if (mult <= 0f || fov <= 0f) return fov * mult;
        return (float)(2.0 * Math.Atan(Math.Tan(fov * Math.PI / 360.0) * mult) * 180.0 / Math.PI);
    }

    public void Run()
    {
        var h = module.GetType("Nova43Camera");
        var cc = module.GetType("CameraController");
        var fixM = cc?.Methods.FirstOrDefault(m => m.Name == "Fix43Y");
        var adj = h?.Methods.FirstOrDefault(m => m.Name == "AdjustY");
        var sf = h?.Methods.FirstOrDefault(m => m.Name == "ScaleFov");
        checks.Section("simulator");
        if (adj == null || sf == null || fixM == null) { checks.Fail("injected methods missing"); return; }
        var env = new Env();

        // ScaleFov
        int bad = 0, n = 0;
        double worstTan = 0;
        foreach (float fov in new[] { 20f, 30f, 40f, 50f, 60f, 70f, 90f })
            foreach (float mult in new[] { 0.5f, 1f, 1.0f / 0.9f, 1.111111f, 1.25f, 4f / 3f, 1.5f, 2f })
            {
                n++;
                float got = (float)D(Run(sf, new object?[] { fov, mult }, env));
                float want = ModelFov(fov, mult);
                if (Math.Abs(got - want) > 1e-3f) bad++;
                // the defining property: the visible half-height at a fixed distance scales by exactly mult
                double ratio = Math.Tan(got * Math.PI / 360.0) / Math.Tan(fov * Math.PI / 360.0);
                worstTan = Math.Max(worstTan, Math.Abs(ratio - mult));
            }
        checks.Check(bad == 0, $"ScaleFov matches the model on {n} cases");
        checks.Check(worstTan < 1e-4, $"ScaleFov: tan(fov'/2)/tan(fov/2) == mult (worst error {worstTan:E1})");
        checks.Check(Math.Abs(D(Run(sf, new object?[] { 50f, 1f }, env)) - 50.0) < 1e-3, "ScaleFov: mult 1 leaves the fov alone");
        checks.Check(D(Run(sf, new object?[] { 50f, 0f }, env)) == 0.0 && D(Run(sf, new object?[] { 0f, 1.3f }, env)) == 0.0, "ScaleFov: degenerate inputs fall back to fov * mult");

        // AdjustY grid vs model
        bad = 0; n = 0;
        var rng = new Random(43);
        foreach (float mult in new[] { 0f, 0.99f, 1f, 1.0001f, 1.111f, 4f / 3f, 1.5f, float.NaN })
            for (int k = 0; k < 600; k++)
            {
                float lo = 8.3f + (float)rng.NextDouble() * 30f;
                float hi = lo + (k % 7 == 0 ? 0f : (float)rng.NextDouble() * 60f - (k % 5 == 0 ? 5f : 0f));
                float y = lo - 10f + (float)rng.NextDouble() * (Math.Abs(hi - lo) + 20f);
                n++;
                float got = (float)D(Run(adj, new object?[] { y, lo, hi, mult }, env));
                float want = ModelAdjustY(y, lo, hi, mult);
                if (!(got == want || Math.Abs(got - want) < 1e-4f)) bad++;
            }
        checks.Check(bad == 0, $"AdjustY matches the model on {n} random cases");

        // property: the 4:3 window never shows more than the 16:9 window did
        float m43 = (16f / 9f) / (4f / 3f);
        int violations = 0, centred = 0, windows = 0;
        for (float sceneH = 14f; sceneH <= 80f; sceneH += 0.7f)
        {
            float lo = HalfH, hi = sceneH - HalfH;
            for (float y = 0; y <= sceneH + 5f; y += 0.37f)
            {
                // the camera starts inside the old clamp (the game's own clamp ran first)
                float y0 = Math.Min(Math.Max(y, lo), Math.Max(lo, hi));
                float r = (float)D(Run(adj, new object?[] { y0, lo, hi, m43 }, env));
                windows++;
                float bottom = r - HalfH * m43, top = r + HalfH * m43;
                bool fits = sceneH >= 2f * HalfH * m43 - 1e-3f;
                if (fits && (bottom < -1e-3f || top > sceneH + 1e-3f)) violations++;
                if (!fits) { centred++; if (Math.Abs(r - sceneH / 2f) > 1e-3f) violations++; }
            }
        }
        checks.Check(violations == 0, $"4:3 view stays inside rooms that fit it, and centres rooms that don't ({windows} windows, {centred} centred)");

        // lock-area windows behave the same: view stays within [min-8.3, max+8.3]
        violations = 0; windows = 0;
        for (float a = 8.3f; a < 60f; a += 3.1f)
            for (float span = 0; span < 30f; span += 1.9f)
                for (float y = a - 3f; y <= a + span + 3f; y += 0.5f)
                {
                    float r = (float)D(Run(adj, new object?[] { Math.Min(Math.Max(y, a), a + span), a, a + span, m43 }, env));
                    windows++;
                    float bottom = r - HalfH * m43, top = r + HalfH * m43;
                    bool fits = (a + span + HalfH) - (a - HalfH) >= 2f * HalfH * m43 - 1e-3f;
                    if (fits && (bottom < a - HalfH - 1e-3f || top > a + span + HalfH + 1e-3f)) violations++;
                }
        checks.Check(violations == 0, $"lock areas: the view never exceeds the 16:9 lock window ({windows} cases)");

        // 16:9 (mult 1) and wider must be a no-op
        bool noop = true;
        for (float y = 0; y < 100; y += 0.9f)
            noop &= D(Run(adj, new object?[] { y, 8.3f, 40f, 1f }, env)) == y;
        checks.Check(noop, "mult 1 (16:9 and wider) leaves the camera exactly as the game had it");

        // Fix43Y: picks lock limits in LOCKED mode, scene limits otherwise
        int locked = Convert.ToInt32(((TypeDefinition)cc!.Fields.First(f => f.Name == "mode").FieldType).Fields.First(f => f.Name == "LOCKED").Constant);
        bad = 0; n = 0;
        env.HeightMult = m43;
        foreach (int mode in new[] { 0, 1, locked, 3, 6 })
            foreach (float y in new[] { 0f, 9f, 12f, 25f, 60f })
            {
                env.Fields["mode"] = mode; env.Fields["yLockMin"] = 20f; env.Fields["yLockMax"] = 30f; env.Fields["yLimit"] = 50f;
                float got = (float)D(Run(fixM, new object?[] { null, y }, env));
                float want = mode == locked ? ModelAdjustY(y, 20f, 30f, m43) : ModelAdjustY(y, HalfH, 50f, m43);
                n++; if (Math.Abs(got - want) > 1e-4f) bad++;
            }
        checks.Check(bad == 0, $"Fix43Y uses the lock window in LOCKED mode and the scene window otherwise ({n} cases)");
    }
}
