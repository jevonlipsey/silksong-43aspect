using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Silksong43.Verify;

/// the three verifier layers, run in order against an open patched module. shared by the standalone
/// harness (verify/Program.cs) and the patcher, which gates its output on it. original is the
/// pristine Assembly-CSharp.dll the patch started from: the semantic layer diffs against it.
public static class VerifyRunner
{
    public static void Run(Checks checks, ModuleDefinition patched, VerifyMode mode, byte[] originalBytes)
    {
        using var origModule = ModuleDefinition.ReadModule(new MemoryStream(originalBytes),
            new ReaderParameters { AssemblyResolver = new NoResolve() });
        var diff = Diff.Compute(origModule, patched);
        new IlVerifier(checks, patched, mode, diff).Run();
        new SemanticVerifier(checks, origModule, patched, diff).Run();
        new Simulator(checks, patched).Run();
    }

    sealed class NoResolve : IAssemblyResolver
    {
        public AssemblyDefinition Resolve(AssemblyNameReference name) => throw new AssemblyResolutionException(name);
        public AssemblyDefinition Resolve(AssemblyNameReference name, ReaderParameters parameters) => throw new AssemblyResolutionException(name);
        public void Dispose() { }
    }
}

/// method-by-method comparison of the original and patched modules
public sealed class Diff
{
    /// full names of methods in the patched module whose body differs from the original or is new
    public HashSet<string> Touched { get; } = new();
    public HashSet<string> AddedTypes { get; } = new();
    public HashSet<string> RemovedTypes { get; } = new();
    public int MethodsCompared { get; private set; }
    public int FieldChanges { get; private set; }

    /// one string per instruction: opcode plus operand, branch targets as instruction indexes
    public static List<string> Sigs(MethodDefinition m)
    {
        var ins = m.Body.Instructions;
        var index = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) index[ins[i]] = i;
        var list = new List<string>();
        foreach (var i in ins)
        {
            var sb = new StringBuilder();
            sb.Append(i.OpCode.Code);
            switch (i.Operand)
            {
                case null: break;
                case Instruction t: sb.Append('@').Append(index[t]); break;
                case Instruction[] ts: sb.Append('@').Append(string.Join(",", ts.Select(t => index[t]))); break;
                case MemberReference r: sb.Append(' ').Append(r.FullName); break;
                case VariableDefinition v: sb.Append(" v").Append(v.Index); break;
                case ParameterDefinition p: sb.Append(" a").Append(p.Index); break;
                case float f: sb.Append(" r4:").Append(BitConverter.SingleToInt32Bits(f)); break;
                case double d: sb.Append(" r8:").Append(BitConverter.DoubleToInt64Bits(d)); break;
                default: sb.Append(' ').Append(i.Operand); break;
            }
            list.Add(sb.ToString());
        }
        return list;
    }

    /// FullName omits generic parameters, so Platform.CreatePlatform() and CreatePlatform<T>() collide without this
    public static string Key(MethodDefinition m) => m.HasGenericParameters ? m.FullName + "`" + m.GenericParameters.Count : m.FullName;

    public static string Fingerprint(MethodDefinition m)
    {
        var sb = new StringBuilder();
        sb.Append(m.ReturnType.FullName).Append('|').Append(m.Attributes).Append('|');
        foreach (var p in m.Parameters) sb.Append(p.ParameterType.FullName).Append(',');
        if (!m.HasBody) return sb.ToString();
        sb.Append('|');
        foreach (var v in m.Body.Variables) sb.Append(v.VariableType.FullName).Append(',');
        sb.Append('|').Append(m.Body.InitLocals);
        var ins = m.Body.Instructions;
        var index = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) index[ins[i]] = i;
        foreach (var i in ins)
        {
            sb.Append(';').Append(i.OpCode.Code);
            switch (i.Operand)
            {
                case null: break;
                case Instruction t: sb.Append('@').Append(index[t]); break;
                case Instruction[] ts: sb.Append('@').Append(string.Join(",", ts.Select(t => index[t]))); break;
                case MemberReference r: sb.Append(r.FullName); break;
                case VariableDefinition v: sb.Append("v").Append(v.Index); break;
                case ParameterDefinition p: sb.Append("a").Append(p.Index); break;
                case float f: sb.Append(BitConverter.SingleToInt32Bits(f)); break;
                case double d: sb.Append(BitConverter.DoubleToInt64Bits(d)); break;
                default: sb.Append(i.Operand); break;
            }
        }
        foreach (var h in m.Body.ExceptionHandlers)
            sb.Append("|eh").Append(h.HandlerType).Append(index[h.TryStart]).Append('-').Append(index[h.HandlerStart]);
        return sb.ToString();
    }

    static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        var stack = new Stack<TypeDefinition>(m.Types);
        while (stack.Count > 0)
        {
            var t = stack.Pop();
            yield return t;
            foreach (var n in t.NestedTypes) stack.Push(n);
        }
    }

    public static Diff Compute(ModuleDefinition orig, ModuleDefinition patched)
    {
        var d = new Diff();
        var origPrints = new Dictionary<string, string>();
        var origFields = new Dictionary<string, int>();
        var origTypes = new HashSet<string>();
        foreach (var t in AllTypes(orig))
        {
            origTypes.Add(t.FullName);
            origFields[t.FullName] = t.Fields.Count;
            foreach (var m in t.Methods) origPrints[Key(m)] = Fingerprint(m);
        }
        var patchedTypes = new HashSet<string>();
        foreach (var t in AllTypes(patched))
        {
            patchedTypes.Add(t.FullName);
            if (origFields.TryGetValue(t.FullName, out var fc) && fc != t.Fields.Count) d.FieldChanges++;
            foreach (var m in t.Methods)
            {
                d.MethodsCompared++;
                if (!origPrints.TryGetValue(Key(m), out var fp) || fp != Fingerprint(m)) d.Touched.Add(Key(m));
            }
        }
        foreach (var t in patchedTypes) if (!origTypes.Contains(t)) d.AddedTypes.Add(t);
        foreach (var t in origTypes) if (!patchedTypes.Contains(t)) d.RemovedTypes.Add(t);
        return d;
    }
}
