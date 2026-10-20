using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Text;
using Events;
using HarmonyLib;
using TMPro;
using UnityEngine;

namespace MoreHeroes
{
    // Events with more than 4 heroes.
    // The event prefab (MapManager.eventPrefab, instantiated for every event) has 4 roll portraits: book/characters/char0..char3
    // (local x 5.6, 4.1, 2.6, 1.1 right to left, y 0, scale 0.9; the characters parent sits at book-local (0.2, 1.5), the book at
    // x 1). Each portrait has children cards (0), num (1), resultTitleKO (2), resultTitleOK (3). EventManager.Awake wires them
    // into characterT / characterTcards / characterSPR / characterTnum and cardKO/cardOK.charTresult (all 4 long), and
    // EventResultManager.CharWinner is a bool[4].
    // With N > 4 heroes the Awake postfix clones char3 for heroes 5..N, grows every one of those arrays to N and lays the
    // portraits out in rows of 4 (heroes 5..8 under heroes 1..4). The roll loops (ShowCharactersRoll, RollCards) follow the
    // number of portraits that exist (ShiftRollSlots) and the outcome loop (FinalResolution) follows the number of heroes that
    // have a CharWinner entry (ShiftEventHeroes), so if growing fails the event stays at 4 instead of indexing past an array.
    internal static class EventsUi
    {
        private static readonly AccessTools.FieldRef<EventManager, Transform[]> CharacterTcards =
            AccessTools.FieldRefAccess<EventManager, Transform[]>("characterTcards");

        private static readonly AccessTools.FieldRef<EventManager, SpriteRenderer[]> CharacterSpr =
            AccessTools.FieldRefAccess<EventManager, SpriteRenderer[]>("characterSPR");

        private static readonly AccessTools.FieldRef<EventManager, TMP_Text[]> CharacterTnum =
            AccessTools.FieldRefAccess<EventManager, TMP_Text[]>("characterTnum");

        private static readonly AccessTools.FieldRef<EventManager, EventResultManager> ResultManager =
            AccessTools.FieldRefAccess<EventManager, EventResultManager>("resultManager");

        private static readonly AccessTools.FieldRef<EventManager, EventReplyData> ReplySelected =
            AccessTools.FieldRefAccess<EventManager, EventReplyData>("replySelected");

        private static readonly AccessTools.FieldRef<EventManager, Reply[]> Replys =
            AccessTools.FieldRefAccess<EventManager, Reply[]>("replys");

        private static readonly AccessTools.FieldRef<Reply, int> ReplyModRollMadness =
            AccessTools.FieldRefAccess<Reply, int>("modRollMadness");

        // Read-only fields: written with reflection (works for instance fields).
        private static readonly FieldInfo RollManagerField = AccessTools.Field(typeof(EventManager), "rollManager");
        private static readonly FieldInfo CharTresultField = AccessTools.Field(typeof(CardHelper), "charTresult");
        private static readonly FieldInfo CharWinnerField = AccessTools.Field(typeof(EventResultManager), "CharWinner");

        private static EventManager boundsLogged;

        // ---- Counts used by the transpiled loops ----

        // Roll portraits that exist beyond 4 and have a hero (ShowCharactersRoll, RollCards loops).
        internal static int RollExtra()
        {
            try
            {
                EventManager em = EventManager.Instance;
                if (em == null || em.Heroes == null || em.characterT == null)
                {
                    return 0;
                }
                int slots = Math.Min(em.characterT.Length, em.Heroes.Length);
                slots = Math.Min(slots, CharacterTcards(em)?.Length ?? 0);
                slots = Math.Min(slots, CharacterSpr(em)?.Length ?? 0);
                slots = Math.Min(slots, CharacterTnum(em)?.Length ?? 0);
                slots = Math.Min(slots, em.cardKO?.charTresult?.Length ?? 0);
                slots = Math.Min(slots, em.cardOK?.charTresult?.Length ?? 0);
                slots = Math.Min(slots, ResultManager(em)?.CharWinner?.Length ?? 0);
                return Math.Max(0, Math.Min(slots, HeroSlots.MaxCount) - HeroSlots.VanillaCount);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // Heroes beyond 4 that have a CharWinner entry (FinalResolution: XP, cards, perks, HP, gold/dust, items per hero).
        internal static int HeroExtra()
        {
            try
            {
                EventManager em = EventManager.Instance;
                if (em == null || em.Heroes == null)
                {
                    return 0;
                }
                int n = Math.Min(em.Heroes.Length, ResultManager(em)?.CharWinner?.Length ?? 0);
                return Math.Max(0, Math.Min(n, HeroSlots.MaxCount) - HeroSlots.VanillaCount);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // Called from transpiled IL.
        public static int ShiftRollSlots(int k)
        {
            return k + RollExtra();
        }

        public static int ShiftEventHeroes(int k)
        {
            return k + HeroExtra();
        }

        // ---- Group rolls: the target scales with the number of heroes ([Balance] ScaleGroupRolls) ----
        // A group roll sums one card cost per hero, so 8 heroes roll about twice the total. The target is scaled by heroes / 4
        // (EventProbability.ScaleGroupTarget) and compared with the raw card total, so the shown cards, the shown target and the
        // result agree. The game's own compare (RollManager.compareByMode) is replaced for the group check in EventResult and in
        // isCritical; single, character and competition rolls are untouched.

        // Heroes that take part in a group roll that is being resolved: one per dealt card.
        private static int RollingHeroes(RollManager rm)
        {
            return rm?.charRollDatas?.Count(d => d?.CardData != null) ?? 0;
        }

        // Heroes that will take part in a group roll shown on a reply (before cards are dealt): heroes with a deck, as in the odds.
        private static int ShownGroupHeroes()
        {
            Hero[] heroes = EventManager.Instance != null ? EventManager.Instance.Heroes : null;
            return heroes?.Count(h => h != null && h.HeroData != null && h.Cards != null && h.Cards.Count > 0) ?? 0;
        }

        // The target a reply shows (vanilla: the reply's number with the madness modifier), and the scaled one.
        private static int VanillaTarget(Enums.RollMode mode, int number, int madness)
        {
            return mode == Enums.RollMode.HigherOrEqual ? number + madness : number - madness;
        }

        // Same rule as RollManager.compareByMode, with the target scaled when group scaling applies.
        private static bool CompareGroup(RollManager rm, Enums.RollMode mode, int total, int number, int heroes)
        {
            bool higher = mode == Enums.RollMode.HigherOrEqual;
            if ((!higher && mode != Enums.RollMode.LowerOrEqual) || !EventProbability.GroupScaling(heroes))
            {
                return rm.compareByMode(mode, total, number);
            }
            int target = EventProbability.ScaleGroupTarget(VanillaTarget(mode, number, rm.modRollMadness), higher, heroes);
            return higher ? total >= target : total <= target;
        }

        // Called from transpiled EventResult instead of rollManager.compareByMode(mode, totalEnergy, SsRollNumber) (group case).
        public static bool CompareGroupRoll(RollManager rm, Enums.RollMode mode, int total, int number)
        {
            try
            {
                int heroes = RollingHeroes(rm);
                bool result = CompareGroup(rm, mode, total, number, heroes);
                if (EventProbability.GroupScaling(heroes) && (mode == Enums.RollMode.HigherOrEqual || mode == Enums.RollMode.LowerOrEqual))
                {
                    int shown = VanillaTarget(mode, number, rm.modRollMadness);
                    int target = EventProbability.ScaleGroupTarget(shown, mode == Enums.RollMode.HigherOrEqual, heroes);
                    Plugin.Log.LogInfo($"Event group roll: card total {total} from {heroes} heroes, needs {mode} {target} " +
                                       $"(vanilla target {shown} x{heroes}/4, [Balance] ScaleGroupRolls): {(result ? "success" : "failure")}.");
                }
                return result;
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Event group roll: scaled check failed, using the vanilla check: {e.Message}");
                return rm.compareByMode(mode, total, number);
            }
        }

        // IL edit for EventResult: the compareByMode call that reads RollManager.totalEnergy (the group case; the single case
        // compares each hero's own result) becomes "call EventsUi.CompareGroupRoll" (same stack: rm, mode, total, number).
        // Changing the instruction in place keeps any label on it.
        internal static string EditGroupCompare(List<CodeInstruction> list)
        {
            FieldInfo total = AccessTools.Field(typeof(RollManager), nameof(RollManager.totalEnergy));
            MethodInfo compare = AccessTools.Method(typeof(RollManager), nameof(RollManager.compareByMode));
            var loads = new List<int>();
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].opcode == OpCodes.Ldfld && list[i].operand is FieldInfo f && f == total)
                {
                    loads.Add(i);
                }
            }
            if (loads.Count != 1)
            {
                return $"expected 1 x ldfld RollManager::totalEnergy, found {loads.Count}";
            }
            int call = -1;
            for (int i = loads[0] + 1; i < list.Count && i <= loads[0] + 8; i++)
            {
                if ((list[i].opcode == OpCodes.Callvirt || list[i].opcode == OpCodes.Call) && list[i].operand is MethodInfo m && m == compare)
                {
                    call = i;
                    break;
                }
            }
            if (call < 0)
            {
                return "no RollManager::compareByMode call within 8 instructions after ldfld RollManager::totalEnergy";
            }
            list[call].opcode = OpCodes.Call;
            list[call].operand = AccessTools.Method(typeof(EventsUi), nameof(CompareGroupRoll));
            return null;
        }

        // Critical success / critical failure of a group roll: same scaled target rule.
        [HarmonyPatch(typeof(RollManager), nameof(RollManager.isCritical))]
        internal static class IsCriticalPatch
        {
            private static bool Prefix(RollManager __instance, EventReplyData replySelected, bool isSuccess, ref bool __result)
            {
                try
                {
                    if (replySelected == null || !replySelected.SsRoll || replySelected.SsRollTarget != Enums.RollTarget.Group)
                    {
                        return true;
                    }
                    int heroes = RollingHeroes(__instance);
                    if (!EventProbability.GroupScaling(heroes))
                    {
                        return true;
                    }
                    int critical = isSuccess ? replySelected.SsRollNumberCritical : replySelected.SsRollNumberCriticalFail;
                    if (critical == -1)
                    {
                        __result = false;
                        return false;
                    }
                    bool meets = CompareGroup(__instance, replySelected.SsRollMode, __instance.totalEnergy, critical, heroes);
                    __result = isSuccess ? meets : !meets;
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Event group roll: scaled critical check failed, using the vanilla check: {e.Message}");
                    return true;
                }
            }
        }

        // The roll box on the selected reply ("<=5"): show the scaled target.
        [HarmonyPatch(typeof(RollManager), nameof(RollManager.ToString), typeof(EventReplyData))]
        internal static class RollBoxTextPatch
        {
            private static void Postfix(RollManager __instance, EventReplyData replySelected, ref string __result)
            {
                try
                {
                    if (replySelected == null || replySelected.SsRollTarget != Enums.RollTarget.Group)
                    {
                        return;
                    }
                    Enums.RollMode mode = replySelected.SsRollMode;
                    bool higher = mode == Enums.RollMode.HigherOrEqual;
                    int heroes = ShownGroupHeroes();
                    if ((!higher && mode != Enums.RollMode.LowerOrEqual) || !EventProbability.GroupScaling(heroes))
                    {
                        return;
                    }
                    int target = EventProbability.ScaleGroupTarget(VanillaTarget(mode, replySelected.SsRollNumber, __instance.modRollMadness), higher, heroes);
                    __result = (higher ? ">=" : "<=") + target;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Event group roll: roll box text failed: {e.Message}");
                }
            }
        }

        // ---- Roll portraits for heroes 5..N ----

        [HarmonyPatch(typeof(EventManager), "Awake")]
        internal static class AwakePatch
        {
            private static void Prefix()
            {
                if (HeroSlots.Expanded)
                {
                    Plugin.ReloadConfig();
                }
            }

            private static void Postfix(EventManager __instance)
            {
                try
                {
                    AddSlots(__instance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Event: adding roll portraits for heroes 5..N failed; rolls show 4 heroes and heroes 5..N get no event effects: {e}");
                }
            }
        }

        private static void AddSlots(EventManager em)
        {
            int n = HeroSlots.Count;
            Transform[] t = em.characterT;
            if (n <= HeroSlots.VanillaCount || t == null)
            {
                return;
            }
            if (t.Length != HeroSlots.VanillaCount || t.Any(x => x == null || x.childCount < 4))
            {
                Plugin.Log.LogError($"Event: expected 4 roll portraits with 4 children each, found {t.Length}; leaving the event at 4 heroes.");
                return;
            }
            Transform template = t[HeroSlots.VanillaCount - 1];
            Transform parent = template.parent;
            var vanillaX = t.Select(x => x.localPosition.x).ToArray();
            float vanillaY = t[0].localPosition.y;
            Vector3 vanillaScale = t[0].localScale;

            var all = new List<Transform>(t);
            for (int i = HeroSlots.VanillaCount; i < n; i++)
            {
                GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
                clone.name = $"char{i} MoreHeroes";
                all.Add(clone.transform);
            }

            // Build every array first, then assign, so a failure leaves the event consistent at 4.
            Transform[] cards = Grow(CharacterTcards(em), n, i => all[i].GetChild(0));
            SpriteRenderer[] spr = Grow(CharacterSpr(em), n, i => all[i].GetComponent<SpriteRenderer>());
            TMP_Text[] num = Grow(CharacterTnum(em), n, i => all[i].GetChild(1).GetComponent<TMP_Text>());
            TMP_Text[] ko = Grow(em.cardKO.charTresult, n, i => all[i].GetChild(2).GetComponent<TMP_Text>());
            TMP_Text[] ok = Grow(em.cardOK.charTresult, n, i => all[i].GetChild(3).GetComponent<TMP_Text>());
            EventResultManager results = ResultManager(em);
            bool[] winners = Grow(results.CharWinner, n, i => false);

            em.characterT = all.ToArray();
            CharacterTcards(em) = cards;
            CharacterSpr(em) = spr;
            CharacterTnum(em) = num;
            CharTresultField.SetValue(em.cardKO, ko);
            CharTresultField.SetValue(em.cardOK, ok);
            CharWinnerField.SetValue(results, winners);

            Layout(all, vanillaX, vanillaY, vanillaScale);
        }

        private static T[] Grow<T>(T[] old, int n, Func<int, T> make)
        {
            if (old == null || old.Length < HeroSlots.VanillaCount)
            {
                throw new InvalidOperationException($"{typeof(T).Name}[] has {old?.Length.ToString() ?? "no"} entries, expected 4");
            }
            var arr = new T[n];
            Array.Copy(old, arr, Math.Min(old.Length, n));
            for (int i = old.Length; i < n; i++)
            {
                arr[i] = make(i);
            }
            return arr;
        }

        // Single-row layout (more than 4 per row). The dealt roll cards, not the portraits, are the widest part of a column: in the
        // 0.2.4 test (scale 0.41, columns 1.15 apart, centre 3.93) the 8 cards spanned about 9.2 world units with the last one past
        // the page edge, so a card is about 1.2 wide at 0.41 (2.9 x scale). The usable page is about 7.4 wide, and the row sat
        // about 0.68 right of the selected reply. Defaults: columns 0.9 apart, scale 0.30 (cards about 0.87 wide, row about 7.2),
        // centred on the selected reply when it is known (RecentreRow), else at local x 3.25.
        private const float SingleRowPitch = 0.9f;
        private const float SingleRowScale = 0.55f * 1.1f / 1.46f * 0.87f / 1.2f;
        private const float SingleRowCentre = 3.25f;
        private const float CardWidthPerScale = 1.2f / (0.55f * 1.1f / 1.46f);

        private static float rowCentreLocalX = float.NaN; // centre of the current single row in the parent's local x, NaN = not single row

        private static void Layout(List<Transform> slots, float[] vanillaX, float vanillaY, Vector3 vanillaScale)
        {
            int n = slots.Count;
            int perRow = Math.Max(HeroSlots.VanillaCount, Math.Min(n, Plugin.EventRollColumns.Value));
            float pitch = Plugin.EventRollRowSpacing.Value;
            float top = vanillaY + Plugin.EventRollOffsetY.Value;
            // Columns: vanilla spacing (1.5, right to left) for rows of 4 (two rows at 0.55 covered the result text below them),
            // or one row across the page (see SingleRowPitch).
            float[] colX = vanillaX;
            float f = 0.55f;
            float columnPitch = 1.5f;
            rowCentreLocalX = float.NaN;
            if (perRow > HeroSlots.VanillaCount)
            {
                columnPitch = Plugin.EventRollColumnSpacing.Value > 0.05f ? Plugin.EventRollColumnSpacing.Value : SingleRowPitch;
                float centre = Math.Abs(Plugin.EventRollCenterX.Value) > 0.001f ? Plugin.EventRollCenterX.Value : SingleRowCentre;
                colX = new float[perRow];
                for (int c = 0; c < perRow; c++)
                {
                    colX[c] = centre + ((perRow - 1) / 2f - c) * columnPitch; // hero 1 on the right, like vanilla
                }
                f = SingleRowScale;
                rowCentreLocalX = centre;
            }
            if (Plugin.EventRollScale.Value > 0.05f)
            {
                f = Plugin.EventRollScale.Value;
            }
            var sb = new StringBuilder();
            sb.Append($"Event: {n} roll portraits in rows of {perRow}, scale {f:0.00} of vanilla, columns {columnPitch:0.00} apart, row spacing {pitch:0.00}, " +
                      $"first row y {top:0.00}; expected roll card width {CardWidthPerScale * f:0.00}, row width {(perRow - 1) * columnPitch + CardWidthPerScale * f:0.00} " +
                      $"(parent '{slots[0].parent?.name}' at world ({slots[0].parent?.position.x:0.00}, {slots[0].parent?.position.y:0.00})).");
            for (int i = 0; i < n; i++)
            {
                int col = i % perRow;
                int row = i / perRow;
                Transform s = slots[i];
                s.localPosition = new Vector3(colX[col], top - row * pitch, s.localPosition.z);
                s.localScale = new Vector3(vanillaScale.x * f, vanillaScale.y * f, vanillaScale.z);
                sb.Append($"\n  hero {i + 1} ({s.name}): local ({s.localPosition.x:0.00}, {s.localPosition.y:0.00}) world ({s.position.x:0.00}, {s.position.y:0.00}) scale {s.localScale.x:0.00}");
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        // When a reply with a roll is picked it has just moved to the top of the right page: centre a single row of roll
        // portraits on its banner (unless [Layout] EventRollCenterX fixes the centre).
        [HarmonyPatch(typeof(EventManager), "SelectOptionResult")]
        internal static class RecentreRowPatch
        {
            private static void Prefix(EventManager __instance)
            {
                try
                {
                    RecentreRow(__instance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Event: centring the roll row on the reply failed, keeping the default centre: {e.Message}");
                }
            }
        }

        private static void RecentreRow(EventManager em)
        {
            if (float.IsNaN(rowCentreLocalX) || Math.Abs(Plugin.EventRollCenterX.Value) > 0.001f || RollExtra() == 0)
            {
                return;
            }
            Reply[] replys = Replys(em);
            if (replys == null || em.optionSelected < 0 || em.optionSelected >= replys.Length || replys[em.optionSelected]?.replyBg == null)
            {
                return;
            }
            Transform parent = em.characterT[0].parent;
            Bounds banner = replys[em.optionSelected].replyBg.bounds;
            float target = parent.InverseTransformPoint(banner.center).x;
            float shift = target - rowCentreLocalX;
            // Keep it on the page if the banner is somewhere unexpected.
            if (Math.Abs(shift) > 1.5f)
            {
                Plugin.Log.LogWarning($"Event: selected reply banner centre at world x {banner.center.x:0.00} (local {target:0.00}) is far from the default row " +
                                      $"centre {rowCentreLocalX:0.00}; keeping the default.");
                return;
            }
            foreach (Transform s in em.characterT)
            {
                s.localPosition += new Vector3(shift, 0f, 0f);
            }
            rowCentreLocalX = target;
            Plugin.Log.LogInfo($"Event: roll row centred on the selected reply (banner world x {banner.min.x:0.00}..{banner.max.x:0.00}, centre local x {target:0.00}, " +
                               $"moved {shift:+0.00;-0.00}).");
        }

        // ---- Logs ----

        [HarmonyPatch(typeof(EventManager), nameof(EventManager.SetEvent))]
        internal static class SetEventLog
        {
            private static void Prefix(EventData _eventData)
            {
                try
                {
                    if (!HeroSlots.Expanded || _eventData == null)
                    {
                        return;
                    }
                    EventData ev = Globals.Instance.GetEventData(_eventData.EventId);
                    if (ev == null || ev.Replys == null)
                    {
                        return;
                    }
                    var rolls = new List<string>();
                    foreach (EventReplyData r in ev.Replys)
                    {
                        if (r != null && r.SsRoll)
                        {
                            rolls.Add(r.SsRollCard != Enums.CardType.None
                                ? $"{r.SsRollTarget} card type {r.SsRollCard}"
                                : $"{r.SsRollTarget} {r.SsRollMode} {r.SsRollNumber}");
                        }
                    }
                    Plugin.Log.LogInfo($"Event '{_eventData.EventId}' with {HeroSlots.Count} heroes: {ev.Replys.Length} replies, rolls: " +
                                       (rolls.Count == 0 ? "none" : string.Join("; ", rolls)) + $"; roll portraits for {EventManager.Instance?.characterT?.Length ?? 0} heroes.");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Event log failed: {e.Message}");
                }
            }
        }

        // When the roll result is evaluated (cards dealt), log where the roll portraits really are, once per event.
        [HarmonyPatch(typeof(EventManager), "EventResult")]
        internal static class EventResultLog
        {
            private static void Prefix(EventManager __instance)
            {
                try
                {
                    if (RollExtra() > 0 && boundsLogged != __instance)
                    {
                        boundsLogged = __instance;
                        __instance.StartCoroutine(LogBoundsLater(__instance));
                    }
                }
                catch (Exception)
                {
                }
            }
        }

        private static IEnumerator LogBoundsLater(EventManager em)
        {
            yield return new WaitForSeconds(0.3f);
            try
            {
                if (em == null)
                {
                    yield break;
                }
                var sb = new StringBuilder("Event roll layout check (renderer bounds, world units):");
                float rowMin = float.MaxValue, rowMax = float.MinValue;
                for (int i = 0; i < em.characterT.Length; i++)
                {
                    Transform s = em.characterT[i];
                    if (s == null || !s.gameObject.activeInHierarchy)
                    {
                        sb.Append($"\n  hero {i + 1}: not shown");
                        continue;
                    }
                    Bounds? b = RewardsUi.VisibleBounds(s);
                    sb.Append(b == null
                        ? $"\n  hero {i + 1}: no visible renderers"
                        : $"\n  hero {i + 1}: x {b.Value.min.x:0.00}..{b.Value.max.x:0.00}, y {b.Value.min.y:0.00}..{b.Value.max.y:0.00}");
                    Transform[] cards = CharacterTcards(em);
                    Bounds? cb = cards != null && i < cards.Length && cards[i] != null ? RewardsUi.VisibleBounds(cards[i]) : null;
                    if (cb != null)
                    {
                        sb.Append($"; dealt card x {cb.Value.min.x:0.00}..{cb.Value.max.x:0.00} ({cb.Value.size.x:0.00} wide), y {cb.Value.min.y:0.00}..{cb.Value.max.y:0.00}");
                        rowMin = Math.Min(rowMin, cb.Value.min.x);
                        rowMax = Math.Max(rowMax, cb.Value.max.x);
                    }
                }
                if (rowMin < rowMax)
                {
                    sb.Append($"\n  dealt cards span x {rowMin:0.00}..{rowMax:0.00} ({rowMax - rowMin:0.00} wide, centre {(rowMin + rowMax) / 2f:0.00})");
                }
                Reply[] replys = Replys(em);
                if (replys != null && em.optionSelected >= 0 && em.optionSelected < replys.Length && replys[em.optionSelected] != null)
                {
                    Bounds? rb = RewardsUi.VisibleBounds(replys[em.optionSelected].transform);
                    if (rb != null)
                    {
                        sb.Append($"\n  selected reply: x {rb.Value.min.x:0.00}..{rb.Value.max.x:0.00}, y {rb.Value.min.y:0.00}..{rb.Value.max.y:0.00}");
                    }
                    SpriteRenderer bg = replys[em.optionSelected].replyBg;
                    if (bg != null)
                    {
                        sb.Append($"\n  selected reply banner: x {bg.bounds.min.x:0.00}..{bg.bounds.max.x:0.00} (centre {bg.bounds.center.x:0.00})");
                    }
                }
                if (em.result != null)
                {
                    sb.Append($"\n  result text at ({em.result.transform.position.x:0.00}, {em.result.transform.position.y:0.00})");
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Event layout check failed: {e.Message}");
            }
        }

        // ---- Sandbox "always pass event rolls": vanilla sets CharWinner[0..3] only ----

        [HarmonyPatch(typeof(EventResultManager), nameof(EventResultManager.OnResult))]
        internal static class OnResultPatch
        {
            private static void Postfix(EventResultManager __instance)
            {
                try
                {
                    if (__instance.CharWinner.Length > HeroSlots.VanillaCount && SandboxManager.Instance != null && SandboxManager.Instance.AlwaysPassEventRoll)
                    {
                        for (int i = 0; i < __instance.CharWinner.Length; i++)
                        {
                            __instance.CharWinner[i] = true;
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Event: sandbox always-pass for heroes 5..N failed: {e.Message}");
                }
            }
        }

        // ---- Reply odds popup ----
        // Single rolls: vanilla lists heroes 1..4 by hand; list every hero. Group rolls: show the scaled target.

        [HarmonyPatch(typeof(Reply), nameof(Reply.Init))]
        internal static class ReplyInitPatch
        {
            private static void Prefix(string _eventId, int _replyIndex)
            {
                EventProbability.CurrentReplyTarget = null;
                try
                {
                    EventReplyData data = Globals.Instance.GetEventData(_eventId)?.Replys?[_replyIndex];
                    if (data != null && data.SsRoll)
                    {
                        EventProbability.CurrentReplyTarget = data.SsRollTarget;
                    }
                }
                catch (Exception)
                {
                }
            }

            private static void Postfix(Reply __instance, string _eventId, int _replyIndex)
            {
                EventProbability.CurrentReplyTarget = null;
                try
                {
                    Hero[] heroes = EventManager.Instance != null ? EventManager.Instance.Heroes : null;
                    if (heroes == null || heroes.Length <= HeroSlots.VanillaCount || __instance.probPopup == null)
                    {
                        return;
                    }
                    EventReplyData data = Globals.Instance.GetEventData(_eventId)?.Replys?[_replyIndex];
                    if (data == null || !data.SsRoll || data.SsRollCard != Enums.CardType.None)
                    {
                        return;
                    }
                    bool higher = data.SsRollMode == Enums.RollMode.HigherOrEqual;
                    if (!higher && data.SsRollMode != Enums.RollMode.LowerOrEqual)
                    {
                        return;
                    }
                    int mod = ReplyModRollMadness(__instance);
                    int target = higher ? data.SsRollNumber + mod : data.SsRollNumber - mod;
                    string space = Functions.SpaceBeforePercentSign() ? " " : "";
                    if (data.SsRollTarget == Enums.RollTarget.Single)
                    {
                        var lines = new List<string>();
                        for (int i = 0; i < heroes.Length; i++)
                        {
                            if (heroes[i] == null || heroes[i].HeroData == null)
                            {
                                continue;
                            }
                            int p = EventManager.Instance.probabilityContainer.GetProbabilitySingle(target, higher, i);
                            lines.Add($"<size=+6><color=#AAA>{heroes[i].SourceName}</color></size> {p}{space}%");
                        }
                        __instance.probPopup.text = "<size=+3>" + string.Format(Texts.Instance.GetText("replyProbability"),
                            "<br><color=#FFAA00><size=+10>" + string.Join("<br>", lines));
                    }
                    else if (data.SsRollTarget == Enums.RollTarget.Group)
                    {
                        // Vanilla already asked GetProbability with the vanilla target (scaled there). Show the scaled target
                        // in the reply banner ("Group [10 or lower]") and say so in the odds popup.
                        int rolling = ShownGroupHeroes();
                        if (!EventProbability.GroupScaling(rolling))
                        {
                            return;
                        }
                        int scaled = EventProbability.ScaleGroupTarget(target, higher, rolling);
                        string vanillaNumber = $"<size=+.2>{target}</size>";
                        TMP_Text banner = __instance.replyRollText;
                        if (banner != null && banner.text != null && banner.text.Contains(vanillaNumber))
                        {
                            banner.text = banner.text.Replace(vanillaNumber, $"<size=+.2>{scaled}</size>");
                        }
                        else
                        {
                            Plugin.Log.LogWarning($"Event '{_eventId}' reply {_replyIndex}: could not find the target {target} in the roll text to show {scaled}.");
                        }
                        if (!string.IsNullOrEmpty(__instance.probPopup.text))
                        {
                            __instance.probPopup.text += $"<br><color=#AAA>({rolling} heroes: target {scaled}, {target} with 4 heroes)</color>";
                        }
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Event: odds popup for heroes 5..N failed: {e.Message}");
                }
            }
        }
    }
}
