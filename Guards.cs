using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace MoreHeroes
{
    // Small guards for screens built for 4 heroes that are not expanded yet. Each one stops a crash for heroes 5..N and
    // leaves the vanilla behaviour for heroes 1-4.
    internal static class Guards
    {
        // The save slot buttons show one portrait per hero from the save's team, into imgHero[4]. With an 8-hero save the loop
        // threw IndexOutOfRange on hero 5, which also aborted MainMenuManager.PositionSaveItems for the remaining buttons
        // (white portraits and stale "incompatible" labels on the save list). The portrait loop is the last thing SetGameData
        // does, so heroes 1-4 are already shown when it throws; swallowing that one exception is equivalent to stopping at 4.
        [HarmonyPatch(typeof(MenuSaveButton), nameof(MenuSaveButton.SetGameData))]
        internal static class SaveSlotPortraitsPatch
        {
            private static Exception Finalizer(Exception __exception, GameData _gameData)
            {
                if (!(__exception is IndexOutOfRangeException))
                {
                    return __exception;
                }
                try
                {
                    Hero[] team = JsonHelper.FromJson<Hero>(_gameData.TeamAtO);
                    if (team != null && team.Length > HeroSlots.VanillaCount)
                    {
                        return null;
                    }
                }
                catch (Exception)
                {
                }
                return __exception;
            }
        }

        // The map/combat side panel (SideCharacters) has 4 portraits unless SidePortraits added more. MatchManager.ShowCharacterWindow
        // highlights the hero's portrait there before opening the character window, which threw for heroes 5..N (clicking their
        // deck pile in combat did nothing). Skip the highlight for heroes without a portrait so the window still opens.
        [HarmonyPatch(typeof(SideCharacters), nameof(SideCharacters.ShowActiveStatus))]
        internal static class SidePanelHighlightPatch
        {
            private static bool Prefix(SideCharacters __instance, int characterIndex)
            {
                return __instance.charArray == null || characterIndex < __instance.charArray.Length;
            }
        }

        // Card and item corruption (CardCraftManager craft types 6 and 7) count tries per hero in a private int[4],
        // corruptionTry[HeroIndex], recreated as new int[4] every time the screen opens (ShowCardCraft). For heroes 5..N,
        // ShowRemainingUses threw IndexOutOfRange inside SelectCharacter -> CreateDeck, so the title switched to the clicked hero
        // but the deck list kept the previous hero's cards. Before every method that indexes it, grow it to the team size,
        // keeping the counts already there.
        [HarmonyPatch]
        internal static class CorruptionTriesPatch
        {
            private static readonly AccessTools.FieldRef<CardCraftManager, int[]> Tries =
                AccessTools.FieldRefAccess<CardCraftManager, int[]>("corruptionTry");

            private static IEnumerable<MethodBase> TargetMethods()
            {
                foreach (string name in new[] { "ShowRemainingUses", "CalculateCorruption", "BuyCorruptionCo", "BuyItemCorruptionCo" })
                {
                    MethodInfo m = AccessTools.Method(typeof(CardCraftManager), name);
                    if (m != null)
                    {
                        yield return m;
                    }
                }
            }

            private static void Prefix(CardCraftManager __instance)
            {
                try
                {
                    int n = HeroSlots.Count;
                    int[] tries = Tries(__instance);
                    if (n <= HeroSlots.VanillaCount || tries == null || tries.Length >= n)
                    {
                        return;
                    }
                    var grown = new int[n];
                    Array.Copy(tries, grown, tries.Length);
                    Tries(__instance) = grown;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Corruption: growing the per-hero tries failed: {e.Message}");
                }
            }
        }
    }
}
