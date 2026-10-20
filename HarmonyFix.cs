using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;

namespace MoreHeroes
{
    // Works around a HarmonyX 2.9 (BepInEx 5.4) bug that changes the behaviour of every patched method with a certain shape.
    // When HarmonyX re-emits a patched method, it drops a "leave" that is the last instruction of a try body (the next
    // instruction starts the handler) or of a catch handler (it carries the end-of-block marker), and lets its emitter add its
    // own "leave" to the end of the exception block instead (ILManipulator / ILEmitter.BeginHandler / EndHandler). That is only
    // the same thing if the original leave jumped to the end of the block. The C# compiler often makes it jump further, e.g. a
    // foreach inside "if (...) {...} else {...}" leaves straight past the else branch. Re-emitted, execution falls into the else.
    // Found with Tactical Thinking (MatchManager.CastCard): after the foreach over the 3 offered cards it ran the "cards from your
    // deck" branch, which set the number of cards to show to the deck count (0), so the window was empty. Without the mod (no
    // patches on CastCard) the card works.
    // Fix: a transpiler that runs after every other transpiler on every patched method and puts a nop between such a leave and
    // the handler (or moves the end-of-block marker from the leave onto a nop after it). The leave then no longer matches the
    // drop rule and is emitted with its own target; the emitter's leave follows it, unreachable. Methods without the pattern are
    // unchanged.
    // Public for mods that depend on MoreHeroes (MorePlayers): they patch more methods after MoreHeroes has loaded and call
    // ApplyToAllPatchedMethods again with their own Harmony instance; methods that already carry the fix are skipped.
    public static class HarmonyFix
    {
        public static int MethodsChecked;
        public static readonly List<string> Fixed = new List<string>();
        public static readonly List<string> Failed = new List<string>();

        public static void ApplyToAllPatchedMethods(Harmony harmony)
        {
            MethodInfo fix = AccessTools.Method(typeof(HarmonyFix), nameof(Transpiler));
            var transpiler = new HarmonyMethod(fix) { priority = Priority.Last };
            foreach (MethodBase m in Harmony.GetAllPatchedMethods().ToList())
            {
                Patches info = Harmony.GetPatchInfo(m);
                if (info != null && info.Transpilers.Any(p => p.PatchMethod == fix))
                {
                    continue;
                }
                MethodsChecked++;
                try
                {
                    harmony.Patch(m, transpiler: transpiler);
                }
                catch (Exception e)
                {
                    Failed.Add($"{m.DeclaringType?.Name}.{m.Name} ({e.GetType().Name}: {e.Message})");
                }
            }
        }

        private static bool IsLeave(CodeInstruction ci)
        {
            return ci.opcode == OpCodes.Leave || ci.opcode == OpCodes.Leave_S;
        }

        private static bool IsBegin(ExceptionBlock b)
        {
            return b.blockType != ExceptionBlockType.EndExceptionBlock;
        }

        // Public so the offline check can run it on the game's IL.
        public static List<CodeInstruction> FixLeaves(List<CodeInstruction> list, out int fixedCount)
        {
            fixedCount = 0;
            for (int i = 0; i < list.Count; i++)
            {
                CodeInstruction ci = list[i];
                if (!IsLeave(ci))
                {
                    continue;
                }
                if (ci.blocks.Count > 0)
                {
                    // End of a catch handler. Leave alone if it also begins a block (a handler that is only a leave).
                    if (ci.blocks.Any(IsBegin))
                    {
                        continue;
                    }
                    var nop = new CodeInstruction(OpCodes.Nop);
                    nop.blocks.AddRange(ci.blocks);
                    ci.blocks.Clear();
                    list.Insert(i + 1, nop);
                    fixedCount++;
                    i++;
                }
                else if (i + 1 < list.Count && list[i + 1].blocks.Count > 0)
                {
                    // End of a try body (or right before another block marker).
                    list.Insert(i + 1, new CodeInstruction(OpCodes.Nop));
                    fixedCount++;
                    i++;
                }
            }
            return list;
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> list = instructions.ToList();
            try
            {
                // Only inserts nops and moves block markers, so the list itself is edited.
                List<CodeInstruction> copy = FixLeaves(list, out int n);
                string name = $"{original?.DeclaringType?.Name}.{original?.Name} ({n})";
                if (n > 0 && !Fixed.Contains(name))
                {
                    Fixed.Add(name);
                }
                return copy;
            }
            catch (Exception e)
            {
                Failed.Add($"{original?.DeclaringType?.Name}.{original?.Name} ({e.GetType().Name}: {e.Message})");
                return list;
            }
        }
    }
}
