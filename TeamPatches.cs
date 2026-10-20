using System;
using System.Collections.Generic;
using BattleMatch;
using HarmonyLib;
using UnityEngine;

namespace MoreHeroes
{
    // Containers sized for 4 heroes that are simpler to fix with a Prefix/Postfix than with a transpiler.
    // All of these are no-ops while HeroSlots.Count == 4.
    internal static class TeamPatches
    {
        private static readonly AccessTools.FieldRef<Team, List<Character>> TeamCharacters =
            AccessTools.FieldRefAccess<Team, List<Character>>("characters");

        private static readonly AccessTools.FieldRef<Team, TeamType> TeamTypeField =
            AccessTools.FieldRefAccess<Team, TeamType>("teamType");

        private static readonly AccessTools.FieldRef<TurnSaveManager, int[]> HeroLifeArr =
            AccessTools.FieldRefAccess<TurnSaveManager, int[]>("heroLifeArr");

        internal static readonly List<string> Applied = new List<string>();
        internal static readonly List<string> Failed = new List<string>();

        internal static void ApplyAll(Harmony harmony)
        {
            Try(harmony, "Team(TeamType) ctor: hero team gets N slots",
                AccessTools.Constructor(typeof(Team), new[] { typeof(TeamType) }), postfix: nameof(TeamResizePostfix));
            Try(harmony, "Team.Clear: hero team keeps N slots",
                AccessTools.Method(typeof(Team), "Clear"), postfix: nameof(TeamResizePostfix));
            Try(harmony, "TurnSaveManager.UpdateHeroLife: grow heroLifeArr",
                AccessTools.Method(typeof(TurnSaveManager), "UpdateHeroLife"), prefix: nameof(UpdateHeroLifePrefix));
            Try(harmony, "TurnSaveManager.GetHeroLifeByIndex: no out-of-range",
                AccessTools.Method(typeof(TurnSaveManager), "GetHeroLifeByIndex"), prefix: nameof(GetHeroLifePrefix));
            Try(harmony, "AtOManager.InitCombatStatsCurrent: grow combatStats rows",
                AccessTools.Method(typeof(AtOManager), "InitCombatStatsCurrent"), prefix: nameof(InitCombatStatsCurrentPrefix));
            Try(harmony, "AtOManager.AddCraftedCard: grow craftedCards",
                AccessTools.Method(typeof(AtOManager), "AddCraftedCard"), prefix: nameof(AddCraftedCardPrefix));
            Try(harmony, "SideCharacters.CharacterIconPosition: clamp index",
                AccessTools.Method(typeof(SideCharacters), "CharacterIconPosition"), prefix: nameof(CharacterIconPositionPrefix));
            Try(harmony, "MatchManager.SetLoadTurn: drop mid-combat resume if FixTOTALCo is unpatched",
                AccessTools.Method(typeof(MatchManager), "SetLoadTurn"), prefix: nameof(SetLoadTurnPrefix));
            Try(harmony, "AtOManager.FinishCardRewards: safety net, auto-pick for heroes without a reward row",
                AccessTools.Method(typeof(AtOManager), "FinishCardRewards"), prefix: nameof(FinishCardRewardsPrefix));
        }

        private static void Try(Harmony harmony, string name, System.Reflection.MethodBase target, string prefix = null, string postfix = null)
        {
            try
            {
                if (target == null)
                {
                    Failed.Add(name + " (method not found)");
                    return;
                }
                harmony.Patch(target,
                    prefix: prefix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(TeamPatches), prefix)),
                    postfix: postfix == null ? null : new HarmonyMethod(AccessTools.Method(typeof(TeamPatches), postfix)));
                Applied.Add(name);
            }
            catch (Exception e)
            {
                Failed.Add($"{name} ({e.GetType().Name}: {e.Message})");
            }
        }

        // BattleMatch.Team always starts with 4 empty slots. The hero team needs one per hero; the NPC team stays at 4.
        private static void TeamResizePostfix(Team __instance)
        {
            try
            {
                if (TeamTypeField(__instance) != TeamType.Hero)
                {
                    return;
                }
                int n = HeroSlots.Count;
                List<Character> chars = TeamCharacters(__instance);
                if (chars != null && chars.Count < n)
                {
                    while (chars.Count < n)
                    {
                        chars.Add(null);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Resizing hero team failed: {e}");
            }
        }

        private static void UpdateHeroLifePrefix(TurnSaveManager __instance, int index)
        {
            ref int[] arr = ref HeroLifeArr(__instance);
            int need = Math.Max(index + 1, HeroSlots.Count);
            if (arr == null)
            {
                arr = new int[need];
            }
            else if (arr.Length < need)
            {
                Array.Resize(ref arr, need);
            }
        }

        // An old turn save may hold fewer entries than heroes. Keep the hero's current HP instead of throwing.
        private static bool GetHeroLifePrefix(TurnSaveManager __instance, int index, ref int __result)
        {
            int[] arr = HeroLifeArr(__instance);
            if (arr != null && index >= 0 && index < arr.Length)
            {
                return true;
            }
            Hero hero = MatchManager.Instance != null ? MatchManager.Instance.GetHero(index) : null;
            __result = hero != null ? Math.Max(1, hero.HpCurrent) : 1;
            Plugin.Log.LogWarning($"Turn save has no HP entry for hero slot {index}; keeping current HP {__result}.");
            return false;
        }

        // combatStats is int[4, 12] from ClearGame (before the team exists) and is indexed by Hero.HeroIndex during combat.
        private static void InitCombatStatsCurrentPrefix(AtOManager __instance)
        {
            try
            {
                int n = HeroSlots.Count;
                int[,] stats = __instance.combatStats;
                if (stats == null || stats.GetLength(0) >= n)
                {
                    return;
                }
                var grown = new int[n, stats.GetLength(1)];
                for (int i = 0; i < stats.GetLength(0); i++)
                {
                    for (int j = 0; j < stats.GetLength(1); j++)
                    {
                        grown[i, j] = stats[i, j];
                    }
                }
                __instance.combatStats = grown;
                Plugin.Log.LogInfo($"combatStats grown to {n} hero rows.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Growing combatStats failed: {e}");
            }
        }

        private static void AddCraftedCardPrefix(AtOManager __instance, int index)
        {
            List<Dictionary<string, int>> list = __instance.craftedCards;
            if (list == null || list.Count == 0)
            {
                return; // the game creates 4 itself; the next call grows it
            }
            int need = Math.Max(index + 1, HeroSlots.Count);
            while (list.Count < need)
            {
                list.Add(new Dictionary<string, int>());
            }
        }

        // Map/town side portraits: SidePortraits adds one per hero; if that failed there are 4, and anything asking for hero
        // 5..8's icon gets the last one.
        private static bool CharacterIconPositionPrefix(SideCharacters __instance, int index, ref Vector3 __result)
        {
            OverCharacter[] arr = __instance.charArray;
            if (arr == null || arr.Length == 0 || (index >= 0 && index < arr.Length))
            {
                return true;
            }
            __result = arr[arr.Length - 1].CharacterIconPosition();
            return false;
        }

        // Safety net: if the FixTOTALCo transpiler could not be applied, a mid-combat save of an expanded team would be decoded
        // with the 4-hero layout and scramble heroes and enemies. Skip the turn data instead, so the fight restarts fresh.
        private static bool SetLoadTurnPrefix()
        {
            if (!HeroSlots.Expanded || IlPatches.FixTotalApplied)
            {
                return true;
            }
            Plugin.Log.LogWarning("Mid-combat save ignored: FixTOTALCo is unpatched and the team has more than 4 heroes. The combat restarts from the beginning.");
            return false;
        }

        // Safety net. Since 0.2.0 the rewards screen has a row per hero (RewardsUi) and cardSelectedArr has an entry per row, so this
        // should not trigger. It covers heroes 5..N that ended up without a pick: the rows could not be cloned (then the array has 4
        // entries and FinishCardRewards would throw IndexOutOfRange on hero 5, which hung the screen in 0.1.0), or a row was never
        // shown. Such a hero gets the first card it was offered (RewardsManager rolls cardsByOrder for every hero), or nothing.
        private static void FinishCardRewardsPrefix(AtOManager __instance, ref string[] arrRewards)
        {
            try
            {
                int size = __instance.team.Size;
                if (arrRewards == null || size <= HeroSlots.VanillaCount)
                {
                    return;
                }
                bool missing = arrRewards.Length < size;
                for (int i = HeroSlots.VanillaCount; i < arrRewards.Length && i < size && !missing; i++)
                {
                    missing = arrRewards[i] == null && __instance.team.GetHero(i)?.HeroData != null;
                }
                if (!missing)
                {
                    return;
                }
                string[] padded = new string[Math.Max(size, arrRewards.Length)];
                Array.Copy(arrRewards, padded, arrRewards.Length);
                Dictionary<int, string[]> offers = RewardsManager.Instance != null ? RewardsManager.Instance.cardsByOrder : null;
                var picks = new List<string>();
                for (int i = HeroSlots.VanillaCount; i < size; i++)
                {
                    if (padded[i] != null)
                    {
                        continue;
                    }
                    string pick = "";
                    if (offers != null && offers.TryGetValue(i, out string[] cards) && cards != null)
                    {
                        foreach (string c in cards)
                        {
                            if (!string.IsNullOrEmpty(c))
                            {
                                pick = c;
                                break;
                            }
                        }
                    }
                    padded[i] = pick;
                    Hero hero = __instance.team.GetHero(i);
                    picks.Add($"{hero?.SourceName ?? "slot " + i}: {(pick == "" ? "nothing" : pick)}");
                }
                arrRewards = padded;
                Plugin.Log.LogWarning("Rewards: some heroes had no reward row, so a card was picked for them automatically (safety net; " +
                                      "the rewards screen should show a row per hero): " + string.Join(", ", picks));
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Padding card rewards failed: {e}");
            }
        }
    }
}
