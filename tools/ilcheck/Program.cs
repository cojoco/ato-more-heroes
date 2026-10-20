using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

// Offline check: run MoreHeroes' (or MorePlayers', NS=MorePlayers) transpilers against the real game IL (no Unity needed) and
// report which apply.
class P
{
    static int Main()
    {
        var managed = Environment.GetEnvironmentVariable("MANAGED");
        AppDomain.CurrentDomain.AssemblyResolve += (s, e) =>
        {
            var n = new AssemblyName(e.Name).Name;
            if (n == "0Harmony") return typeof(Harmony).Assembly;
            string mod = Environment.GetEnvironmentVariable("MOD");
            foreach (var dir in new[] { managed, managed + "/../../BepInEx/core", System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(mod)),
                         managed + "/../../BepInEx/plugins" })
            {
                var p = System.IO.Path.Combine(dir, n + ".dll");
                if (System.IO.File.Exists(p)) return Assembly.LoadFrom(p);
            }
            return null;
        };
        return Run();
    }

    static bool IsLeave(CodeInstruction c) => c.opcode == System.Reflection.Emit.OpCodes.Leave || c.opcode == System.Reflection.Emit.OpCodes.Leave_S;

    // Drops under the HarmonyX rule whose target is not the instruction right after the block's end.
    static int HarmfulDrops(List<CodeInstruction> list)
    {
        int harmful = 0;
        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];
            if (!IsLeave(c)) continue;
            bool drop = c.blocks.Count > 0 || (i + 1 < list.Count && list[i + 1].blocks.Count > 0);
            if (!drop) continue;
            // find the end of the exception block this leave ends: scan forward from i (handler start at i+1 or end on i)
            int depth = 0, end = -1;
            for (int j = i; j < list.Count && end < 0; j++)
            {
                foreach (var b in list[j].blocks)
                {
                    if (j == i + 1 || j == i)
                    {
                        if (b.blockType == ExceptionBlockType.EndExceptionBlock && depth == 0) { end = j; break; }
                        if (b.blockType == ExceptionBlockType.BeginExceptionBlock) depth++;
                        continue;
                    }
                    if (b.blockType == ExceptionBlockType.BeginExceptionBlock) depth++;
                    else if (b.blockType == ExceptionBlockType.EndExceptionBlock) { if (depth == 0) { end = j; break; } depth--; }
                }
            }
            if (end < 0 || end + 1 >= list.Count) { harmful++; continue; }
            var targetLabel = (System.Reflection.Emit.Label)c.operand;
            if (!list[end + 1].labels.Contains(targetLabel)) harmful++;
        }
        return harmful;
    }

    static int Run()
    {
        string ns = Environment.GetEnvironmentVariable("NS") ?? "MoreHeroes";
        Assembly mh = Assembly.LoadFrom(Environment.GetEnvironmentVariable("MOD"));
        Type plugin = mh.GetType(ns + ".Plugin");
        plugin.GetField("Log", BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, new BepInEx.Logging.ManualLogSource("check"));
        Type il = mh.GetType(ns + ".IlPatches");
        il.GetMethod("Define", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, null);
        var specs = (IList)il.GetField("Specs", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        var byMethod = (IDictionary)il.GetField("ByMethod", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        MethodInfo transpiler = il.GetMethod("Transpiler", BindingFlags.NonPublic | BindingFlags.Static);
        int ok = 0;
        foreach (object spec in specs)
        {
            Type st = spec.GetType();
            string name = (string)st.GetField("Name").GetValue(spec);
            var resolve = (Func<MethodBase>)st.GetField("Resolve").GetValue(spec);
            MethodBase target = resolve();
            if (target == null) { Console.WriteLine($"[MISSING] {name}"); continue; }
            byMethod[target] = spec;
            List<CodeInstruction> orig = PatchProcessor.GetOriginalInstructions(target);
            var result = ((IEnumerable<CodeInstruction>)transpiler.Invoke(null, new object[] { orig, target })).ToList();
            bool applied = (bool)st.GetField("Applied").GetValue(spec);
            if (applied) ok++;
            Console.WriteLine($"[{(applied ? "ok" : "SKIPPED")}] {name}: {st.GetField("Result").GetValue(spec)}");
            string dump = Environment.GetEnvironmentVariable("DUMP");
            if (dump != null && name == dump)
            {
                System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("OUT") + ".patched", result.Select((x, i) => $"{i}: {x} {string.Join(",", x.labels.Select(l => l.GetHashCode()))}"));
                System.IO.File.WriteAllLines(Environment.GetEnvironmentVariable("OUT") + ".orig", PatchProcessor.GetOriginalInstructions(target).Select((x, i) => $"{i}: {x}"));
            }
            string show = Environment.GetEnvironmentVariable("SHOW");
            if (applied && show != null && name.Contains(show))
            {
                for (int i = 0; i < result.Count; i++)
                {
                    if (result[i].operand is MethodInfo m && (m.DeclaringType?.Namespace == "MoreHeroes" || m.DeclaringType?.Namespace == ns))
                    {
                        for (int j = Math.Max(0, i - 4); j <= Math.Min(result.Count - 1, i + 2); j++)
                            Console.WriteLine($"    {j}: {result[j]}");
                        Console.WriteLine("    --");
                    }
                }
            }
        }
        Console.WriteLine($"{ok}/{specs.Count} applied");

        // HarmonyX 2.9 drop rule: a leave is dropped (and replaced by a leave to the end of its exception block) if it has block
        // markers or the next instruction has. Count drops whose original target is not the end of the block, before/after the fix.
        Type fix = typeof(object).Assembly == null ? null : AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType("MoreHeroes.HarmonyFix")).FirstOrDefault(t => t != null)
                   ?? Assembly.Load("MoreHeroes").GetType("MoreHeroes.HarmonyFix");
        MethodInfo fixLeaves = fix.GetMethod("FixLeaves");
        foreach (object spec in specs)
        {
            Type st = spec.GetType();
            MethodBase target = ((Func<MethodBase>)st.GetField("Resolve").GetValue(spec))();
            var orig = PatchProcessor.GetOriginalInstructions(target);
            int before = HarmfulDrops(orig);
            var args = new object[] { PatchProcessor.GetOriginalInstructions(target), 0 };
            var fixedList = (List<CodeInstruction>)fixLeaves.Invoke(null, args);
            int after = HarmfulDrops(fixedList);
            int anyAfter = fixedList.Select((x, i) => (x, i)).Count(t => IsLeave(t.x) && (t.x.blocks.Count > 0 || (t.i + 1 < fixedList.Count && fixedList[t.i + 1].blocks.Count > 0)));
            if (before > 0 || after > 0 || (int)args[1] > 0)
                Console.WriteLine($"  leave check {st.GetField("Name").GetValue(spec)}: harmful drops {before} -> {after}, nops added {args[1]}, droppable leaves left {anyAfter}");
        }
        return ok == specs.Count ? 0 : 1;
    }
}
