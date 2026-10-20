using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;

namespace MoreHeroes
{
    // Event roll odds with more than 4 heroes.
    // Vanilla ProbabilityContainer.SetProbability lists every combination of one card per hero (each deck capped at 50 cards) and
    // stores every sum in a List<int>; GetProbability then counts the entries that meet the target. That is at most 50^4 entries
    // with 4 heroes, but 15^8 (2.5 billion) with 8 starting decks: the list runs out of memory and the event never opens.
    // With more than 4 heroes the sum distribution is built by convolution instead (one hero at a time, a few hundred buckets) and
    // GetProbability reads it directly. Same result as the enumeration, except that the whole deck is used (the actual roll draws
    // from the whole deck; the 50-card cap was only there to keep the enumeration small).
    // With 4 heroes both methods run as vanilla.
    internal static class EventProbability
    {
        private static readonly AccessTools.FieldRef<ProbabilityContainer, IReadOnlyList<IReadOnlyList<string>>> HeroCards =
            AccessTools.FieldRefAccess<ProbabilityContainer, IReadOnlyList<IReadOnlyList<string>>>("heroCards");

        private static readonly AccessTools.FieldRef<ProbabilityContainer, List<int>> ProbabilityList =
            AccessTools.FieldRefAccess<ProbabilityContainer, List<int>>("probability");

        private const int VanillaDeckCap = 50;
        private const double VanillaMaxCombinations = 6250000d; // 50^4: the most the vanilla enumeration ever does

        // The distribution for the container it was built for: sum -> probability. Null = vanilla list in use.
        private static ProbabilityContainer owner;
        private static Dictionary<int, double> distribution;
        private static int rollingHeroes;

        // Set while Reply.Init runs (see EventsUi.ReplyInitPatch), so GetProbability knows which roll it is asked about.
        internal static Enums.RollTarget? CurrentReplyTarget;

        [HarmonyPatch(typeof(ProbabilityContainer), "SetProbability")]
        internal static class SetProbabilityPatch
        {
            private static bool Prefix(ProbabilityContainer __instance)
            {
                try
                {
                    IReadOnlyList<IReadOnlyList<string>> cards = HeroCards(__instance);
                    if (cards == null)
                    {
                        Clear();
                        return true;
                    }
                    double combinations = 1d;
                    foreach (IReadOnlyList<string> deck in cards)
                    {
                        int n = Math.Min(deck?.Count ?? 0, VanillaDeckCap);
                        if (n > 0)
                        {
                            combinations *= n;
                        }
                    }
                    if (cards.Count <= HeroSlots.VanillaCount && combinations <= VanillaMaxCombinations)
                    {
                        Clear();
                        return true;
                    }

                    var watch = Stopwatch.StartNew();
                    Dictionary<int, double> dist = Build(cards, out int heroes);
                    watch.Stop();
                    owner = __instance;
                    distribution = dist;
                    rollingHeroes = heroes;
                    // Leave the vanilla list empty (not null) so nothing else can trip over it.
                    List<int> list = ProbabilityList(__instance);
                    if (list == null)
                    {
                        ProbabilityList(__instance) = new List<int>();
                    }
                    else
                    {
                        list.Clear();
                    }
                    int minSum = int.MaxValue, maxSum = int.MinValue;
                    foreach (int s in dist.Keys)
                    {
                        minSum = Math.Min(minSum, s);
                        maxSum = Math.Max(maxSum, s);
                    }
                    Plugin.Log.LogInfo($"Event odds: {cards.Count} hero decks, {heroes} rolling, {combinations:0.###e0} card combinations (vanilla would list them all); " +
                                       $"sum distribution built in {watch.Elapsed.TotalMilliseconds:0.0} ms, {dist.Count} sums ({minSum}..{maxSum}). " +
                                       $"Group roll targets {(GroupScaling(heroes) ? $"scaled x{heroes}/4" : "not scaled")}.");
                    return false;
                }
                catch (Exception e)
                {
                    // Never fall back to the enumeration with a big team: it is what runs out of memory.
                    Plugin.Log.LogError($"Event odds: building the sum distribution failed, the shown percentages will be wrong: {e}");
                    Clear();
                    List<int> list = ProbabilityList(__instance);
                    if (list != null)
                    {
                        list.Clear();
                        list.Add(0);
                    }
                    return HeroCards(__instance) == null || HeroCards(__instance).Count <= HeroSlots.VanillaCount;
                }
            }
        }

        [HarmonyPatch(typeof(ProbabilityContainer), "GetProbability")]
        internal static class GetProbabilityPatch
        {
            private static bool Prefix(ProbabilityContainer __instance, int result, bool higherOrEqual, ref int __result)
            {
                try
                {
                    if (distribution == null || owner != __instance)
                    {
                        return true;
                    }
                    if (AtOManager.Instance != null && AtOManager.Instance.currentMapNode == "tutorial_2")
                    {
                        __result = 100;
                        return false;
                    }
                    // Vanilla only asks for group rolls here; scale the target unless we know it is something else.
                    bool scale = GroupScaling(rollingHeroes) && (CurrentReplyTarget == null || CurrentReplyTarget == Enums.RollTarget.Group);
                    int target = scale ? ScaleGroupTarget(result, higherOrEqual, rollingHeroes) : result;
                    double p = 0d;
                    foreach (KeyValuePair<int, double> kv in distribution)
                    {
                        int sum = kv.Key;
                        if (sum == target || (higherOrEqual && sum > target) || (!higherOrEqual && sum < target))
                        {
                            p += kv.Value;
                        }
                    }
                    // Vanilla: Clamp(CeilToInt(times / total * 100), 0, 100). The epsilon keeps exact values (e.g. 50%) from rounding up.
                    __result = Math.Max(0, Math.Min(100, (int)Math.Ceiling(p * 100d - 1e-7)));
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Event odds: GetProbability failed: {e.Message}");
                    __result = 0;
                    return false;
                }
            }
        }

        private static void Clear()
        {
            owner = null;
            distribution = null;
            rollingHeroes = 0;
        }

        // Sum distribution of one random card cost per hero (each card of the filtered deck equally likely), built hero by hero.
        private static Dictionary<int, double> Build(IReadOnlyList<IReadOnlyList<string>> cards, out int heroes)
        {
            var dist = new Dictionary<int, double> { [0] = 1d };
            heroes = 0;
            foreach (IReadOnlyList<string> deck in cards)
            {
                if (deck == null || deck.Count == 0)
                {
                    continue; // vanilla skips a hero with no cards (adds 0)
                }
                heroes++;
                var costs = new Dictionary<int, double>();
                double each = 1d / deck.Count;
                foreach (string card in deck)
                {
                    int c = Cost(card);
                    costs[c] = (costs.TryGetValue(c, out double v) ? v : 0d) + each;
                }
                var next = new Dictionary<int, double>();
                foreach (KeyValuePair<int, double> a in dist)
                {
                    foreach (KeyValuePair<int, double> b in costs)
                    {
                        int s = a.Key + b.Key;
                        next[s] = (next.TryGetValue(s, out double v) ? v : 0d) + a.Value * b.Value;
                    }
                }
                dist = next;
            }
            return dist;
        }

        // Same lookup as the game: the cached cost table, else the card's energy cost (what the actual roll reads).
        private static int Cost(string card)
        {
            Dictionary<string, int> table = Globals.Instance.CardEnergyCost;
            if (table != null && table.TryGetValue(card, out int cost))
            {
                return cost;
            }
            var data = Globals.Instance.GetCardData(card, instantiate: false);
            return data != null ? data.EnergyCost : 0;
        }

        internal static bool GroupScaling(int heroes)
        {
            return heroes > HeroSlots.VanillaCount && Plugin.ScaleGroupRolls.Value;
        }

        // Group roll target (already including the madness modifier) scaled to the number of rolling heroes: target * heroes / 4,
        // compared with the raw card total. 8 heroes: exactly x2 ("5 or lower" becomes "10 or lower"). For 5-7 heroes "X or lower"
        // rounds down and "X or higher" rounds up, which is exactly "total * 4 / heroes meets the vanilla target" without rounding,
        // so the difficulty stays as close to vanilla as a whole-number target allows. Used for the shown target, the shown odds,
        // the actual roll and the critical checks, so they always agree.
        internal static int ScaleGroupTarget(int target, bool higherOrEqual, int heroes)
        {
            if (heroes <= HeroSlots.VanillaCount)
            {
                return target;
            }
            long num = (long)target * heroes;
            long q = num / HeroSlots.VanillaCount;
            long r = num % HeroSlots.VanillaCount;
            if (r != 0)
            {
                if (num < 0)
                {
                    q--; // q is now the floor
                }
                if (higherOrEqual)
                {
                    q++; // ceiling
                }
            }
            return (int)q;
        }
    }
}
