using System;
using System.Collections.Generic;
using System.Linq;
using Mono.Cecil;
using Mono.Cecil.Cil;

namespace Silksong43.Verify;

/// structural verifier. it re-derives every stack height of every touched method (changed or new
/// versus the original) from the instruction stream, checks every branch lands on an instruction,
/// resolves every member the touched methods use, checks visibility, and rejects any reference that
/// would drag the patcher's .NET runtime into Unity's Mono: such a dll builds fine and then fails at
/// JIT on the device (chefrpg v5.4 shipped like that).
///
/// in ReferenceFree mode there is no Unity assembly to resolve against: scope bans, stack, branch and
/// visibility checks are unchanged; a member in this module still resolves; a member in another
/// assembly must be scoped to a declared assembly reference and its resolve is counted as skipped.
public sealed class IlVerifier
{
    // anything scoped to one of these is the patcher's runtime leaking in, not Unity's Mono
    static readonly string[] Banned =
    {
        "System.Private.CoreLib", "System.Runtime", "System.Collections", "System.Linq", "System.Reflection",
        "System.Runtime.Extensions", "System.Private.Uri", "System.Console",
    };

    readonly Checks checks;
    readonly ModuleDefinition module;
    readonly VerifyMode mode;
    readonly Diff diff;
    int skipped;

    public IlVerifier(Checks checks, ModuleDefinition module, VerifyMode mode, Diff diff)
    {
        this.checks = checks; this.module = module; this.mode = mode; this.diff = diff;
    }

    static string ScopeName(IMetadataScope? s) => s switch
    {
        AssemblyNameReference a => a.Name,
        ModuleDefinition md => md.Assembly?.Name.Name ?? md.Name,
        ModuleReference mr => mr.Name,
        _ => "?",
    };

    static IEnumerable<TypeReference> Flatten(TypeReference t)
    {
        yield return t;
        if (t is GenericInstanceType g) foreach (var a in g.GenericArguments) foreach (var x in Flatten(a)) yield return x;
        if (t is TypeSpecification s && t is not GenericInstanceType) foreach (var x in Flatten(s.ElementType)) yield return x;
    }

    public void Run()
    {
        checks.Section("assembly structure");
        var refs = module.AssemblyReferences.Select(r => r.Name).ToList();
        foreach (var b in Banned)
            checks.Check(!refs.Contains(b), $"no {b} assembly reference");
        checks.Check(refs.Contains("netstandard"), "unity-compatible core library present (netstandard)");

        // every member reference in the whole module: scope must not be banned. this is the pitfall
        // check for the entire dll, not just our methods.
        var badScopes = new List<string>();
        int memberRefs = 0;
        foreach (var mr in module.GetMemberReferences())
        {
            memberRefs++;
            var scope = mr.DeclaringType != null ? ScopeName(mr.DeclaringType.Scope) : "?";
            if (Banned.Contains(scope)) badScopes.Add($"{mr.FullName} -> {scope}");
        }
        foreach (var tr in module.GetTypeReferences())
            if (Banned.Contains(ScopeName(tr.Scope))) badScopes.Add($"type {tr.FullName} -> {ScopeName(tr.Scope)}");
        checks.Check(badScopes.Count == 0,
            badScopes.Count == 0 ? $"none of {memberRefs} member references is scoped to the host .NET runtime"
                                 : $"host-runtime references: {string.Join("; ", badScopes.Take(5))}");

        checks.Section($"touched methods ({diff.Touched.Count})");
        var byName = new Dictionary<string, MethodDefinition>();
        foreach (var t in AllTypes(module)) foreach (var m in t.Methods) if (diff.Touched.Contains(Diff.Key(m))) byName[Diff.Key(m)] = m;
        foreach (var kv in byName.OrderBy(k => k.Key)) VerifyMethod(kv.Value);
        if (skipped > 0) checks.Skip($"{skipped} external member resolves (no Unity assemblies in this mode)");
    }

    static IEnumerable<TypeDefinition> AllTypes(ModuleDefinition m)
    {
        var s = new Stack<TypeDefinition>(m.Types);
        while (s.Count > 0) { var t = s.Pop(); yield return t; foreach (var n in t.NestedTypes) s.Push(n); }
    }

    bool CheckScope(string what, TypeReference? t, List<string> errors)
    {
        if (t == null) return true;
        foreach (var x in Flatten(t))
        {
            if (x is GenericParameter) continue;
            var scope = ScopeName(x.Scope);
            if (Banned.Contains(scope)) { errors.Add($"{what}: {x.FullName} scoped to {scope}"); return false; }
            if (x.Scope is AssemblyNameReference a && !module.AssemblyReferences.Contains(a)) { errors.Add($"{what}: {x.FullName} scope {a.Name} is not a declared reference"); return false; }
        }
        return true;
    }

    void VerifyMethod(MethodDefinition m)
    {
        var errors = new List<string>();
        var name = m.FullName;
        if (!m.HasBody) { checks.Fail($"{name}: touched method has no body"); return; }
        var body = m.Body;
        var ins = body.Instructions;
        var set = new HashSet<Instruction>(ins);

        CheckScope("return", m.ReturnType, errors);
        foreach (var p in m.Parameters) CheckScope("param " + p.Name, p.ParameterType, errors);
        foreach (var v in body.Variables) CheckScope("local " + v.Index, v.VariableType, errors);

        int resolved = 0;
        foreach (var i in ins)
        {
            switch (i.Operand)
            {
                case Instruction t when !set.Contains(t): errors.Add($"{i}: branch target outside the body"); break;
                case Instruction[] ts when ts.Any(t => !set.Contains(t)): errors.Add($"{i}: switch target outside the body"); break;
                case VariableDefinition v when body.Variables.IndexOf(v) < 0: errors.Add($"{i}: local not in body"); break;
                case ParameterDefinition p when p.Index < -1 || p.Index >= m.Parameters.Count: errors.Add($"{i}: parameter out of range"); break;
                case MethodReference mr:
                    CheckScope(mr.FullName, mr.DeclaringType, errors);
                    CheckScope(mr.FullName + " ret", mr.ReturnType, errors);
                    resolved += ResolveMember(mr, errors, m);
                    break;
                case FieldReference fr:
                    CheckScope(fr.FullName, fr.DeclaringType, errors);
                    CheckScope(fr.FullName + " type", fr.FieldType, errors);
                    resolved += ResolveMember(fr, errors, m);
                    break;
                case TypeReference tr:
                    CheckScope(tr.FullName, tr, errors);
                    break;
            }
        }

        // stack and branch structure
        foreach (var e in StackCheck(m)) errors.Add(e);

        checks.Check(errors.Count == 0,
            errors.Count == 0 ? $"{name}: {ins.Count} instructions, stack/branches/scopes ok, {resolved} members resolved"
                              : $"{name}: {string.Join(" | ", errors.Take(4))}");
    }

    int ResolveMember(MemberReference mr, List<string> errors, MethodDefinition user)
    {
        bool local = mr.DeclaringType is TypeDefinition || ReferenceEquals(mr.DeclaringType?.Scope, module);
        IMemberDefinition? def = null;
        try
        {
            def = mr switch { MethodReference m => m.Resolve(), FieldReference f => f.Resolve(), _ => null };
        }
        catch (AssemblyResolutionException) { }
        if (def == null)
        {
            if (mode == VerifyMode.ReferenceFree && !local) { skipped++; return 0; }
            errors.Add($"unresolved member {mr.FullName}");
            return 0;
        }
        // visibility: Mono throws Method/FieldAccessException when private/internal members are used across types
        bool sameType = user.DeclaringType == def.DeclaringType || def.DeclaringType.DeclaringType == user.DeclaringType || user.DeclaringType.DeclaringType == def.DeclaringType;
        bool sameModule = def.DeclaringType.Module == user.Module;
        bool accessible = def switch
        {
            MethodDefinition md => md.IsPublic || (sameModule && (md.IsAssembly || md.IsFamilyOrAssembly)),
            FieldDefinition fd => fd.IsPublic || (sameModule && (fd.IsAssembly || fd.IsFamilyOrAssembly)),
            _ => true,
        };
        if (!sameType && !accessible) errors.Add($"{mr.FullName} is not accessible from {user.DeclaringType.Name}");
        return 1;
    }

    // ---- stack height analysis ----

    List<string> StackCheck(MethodDefinition m)
    {
        var errors = new List<string>();
        var ins = m.Body.Instructions;
        if (ins.Count == 0) { errors.Add("empty body"); return errors; }
        var depth = new int[ins.Count];
        Array.Fill(depth, -1);
        var idx = new Dictionary<Instruction, int>();
        for (int i = 0; i < ins.Count; i++) idx[ins[i]] = i;
        var work = new Stack<int>();
        void Enter(int at, int d, string why)
        {
            if (at >= ins.Count) { errors.Add($"{why}: falls off the end"); return; }
            if (depth[at] == -1) { depth[at] = d; work.Push(at); }
            else if (depth[at] != d) errors.Add($"IL_{ins[at].Offset:x4}: stack depth {d} vs {depth[at]} on merge");
        }
        Enter(0, 0, "entry");
        foreach (var h in m.Body.ExceptionHandlers)
        {
            Enter(idx[h.HandlerStart], h.HandlerType == ExceptionHandlerType.Catch || h.HandlerType == ExceptionHandlerType.Filter ? 1 : 0, "handler");
            if (h.FilterStart != null) Enter(idx[h.FilterStart], 1, "filter");
        }
        int maxDepth = 0;
        while (work.Count > 0 && errors.Count < 8)
        {
            int at = work.Pop();
            int d = depth[at];
            var i = ins[at];
            int pop = Pops(i, m), push = Pushes(i, m);
            if (d < pop) { errors.Add($"IL_{i.Offset:x4} {i.OpCode}: stack underflow (have {d}, needs {pop})"); continue; }
            int nd = d - pop + push;
            maxDepth = Math.Max(maxDepth, nd);
            switch (i.OpCode.FlowControl)
            {
                case FlowControl.Return:
                    if (nd != 0) errors.Add($"IL_{i.Offset:x4}: ret leaves {nd} value(s) on the stack");
                    break;
                case FlowControl.Throw: break;
                case FlowControl.Branch:
                    Enter(idx[(Instruction)i.Operand], nd, $"IL_{i.Offset:x4}");
                    break;
                case FlowControl.Cond_Branch:
                    if (i.Operand is Instruction[] ts) foreach (var t in ts) Enter(idx[t], nd, $"IL_{i.Offset:x4}");
                    else Enter(idx[(Instruction)i.Operand], nd, $"IL_{i.Offset:x4}");
                    Enter(at + 1, nd, $"IL_{i.Offset:x4}");
                    break;
                default:
                    if (i.OpCode == OpCodes.Leave || i.OpCode == OpCodes.Leave_S) Enter(idx[(Instruction)i.Operand], 0, $"IL_{i.Offset:x4}");
                    else if (i.OpCode == OpCodes.Endfinally || i.OpCode == OpCodes.Endfilter) { }
                    else Enter(at + 1, nd, $"IL_{i.Offset:x4}");
                    break;
            }
        }
        // every instruction must be reachable (no dead code left by a bad retarget)
        for (int i = 0; i < ins.Count && errors.Count < 8; i++)
            if (depth[i] == -1) errors.Add($"IL_{ins[i].Offset:x4} {ins[i].OpCode}: unreachable");
        return errors;
    }

    static int Pops(Instruction i, MethodDefinition m)
    {
        switch (i.OpCode.StackBehaviourPop)
        {
            case StackBehaviour.Pop0: return 0;
            case StackBehaviour.Pop1: case StackBehaviour.Popi: case StackBehaviour.Popref: return 1;
            case StackBehaviour.Pop1_pop1: case StackBehaviour.Popi_pop1: case StackBehaviour.Popi_popi: case StackBehaviour.Popi_popi8:
            case StackBehaviour.Popi_popr4: case StackBehaviour.Popi_popr8: case StackBehaviour.Popref_pop1: case StackBehaviour.Popref_popi: return 2;
            case StackBehaviour.Popi_popi_popi: case StackBehaviour.Popref_popi_popi: case StackBehaviour.Popref_popi_popi8:
            case StackBehaviour.Popref_popi_popr4: case StackBehaviour.Popref_popi_popr8: case StackBehaviour.Popref_popi_popref: return 3;
            case StackBehaviour.Varpop:
                if (i.OpCode.Code == Code.Ret) return m.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
                if (i.Operand is MethodReference mr)
                {
                    int n = mr.Parameters.Count;
                    if (mr.HasThis && i.OpCode.Code != Code.Newobj) n++;
                    return n;
                }
                if (i.Operand is CallSite cs) return cs.Parameters.Count + 1 + (cs.HasThis ? 1 : 0);
                return 0;
        }
        return 0;
    }

    static int Pushes(Instruction i, MethodDefinition m)
    {
        switch (i.OpCode.StackBehaviourPush)
        {
            case StackBehaviour.Push0: return 0;
            case StackBehaviour.Push1: case StackBehaviour.Pushi: case StackBehaviour.Pushi8: case StackBehaviour.Pushr4: case StackBehaviour.Pushr8: case StackBehaviour.Pushref: return 1;
            case StackBehaviour.Push1_push1: return 2;
            case StackBehaviour.Varpush:
                if (i.Operand is MethodReference mr)
                    return i.OpCode.Code == Code.Newobj ? 1 : (mr.ReturnType.MetadataType == MetadataType.Void ? 0 : 1);
                if (i.Operand is CallSite cs) return cs.ReturnType.MetadataType == MetadataType.Void ? 0 : 1;
                return 1;
        }
        return 0;
    }
}
