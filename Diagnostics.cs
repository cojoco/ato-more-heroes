using System;
using System.Collections;
using System.Linq;
using System.Text;
using BattleMatch;
using HarmonyLib;
using UnityEngine;

namespace MoreHeroes
{
    // Release builds of the game turn Unity logging off in Globals.Awake (Debug.unityLogger.logEnabled = Debug.isDebugBuild),
    // so exceptions thrown by game code are never logged and a broken screen just hangs silently. Re-enabled, they go to Unity's
    // Player.log (not BepInEx/LogOutput.log; Player.log also mirrors the BepInEx lines).
    // Turn it back on, filtered to warnings, errors and exceptions so the game's own Debug.Log chatter stays out.
    internal static class Diagnostics
    {
        internal static void EnableGameErrorLog()
        {
            if (!Plugin.ShowGameErrors.Value)
            {
                return;
            }
            Debug.unityLogger.logEnabled = true;
            Debug.unityLogger.filterLogType = LogType.Warning;
        }

        [HarmonyPatch(typeof(Globals), "Awake")]
        internal static class GlobalsAwakePatch
        {
            private static void Postfix()
            {
                EnableGameErrorLog();
            }
        }

        // 0.2.4 test: a card that lets hero 5..8 look through a deck (Expert Tracker / Trace: look at the top cards, discard any;
        // Tactical Thinking: pick 1 of 3) opened its window with no cards, without an exception. The code path reads no
        // 4-sized array that could explain it, so log what the window is asked to show and, a moment later, what it holds.
        [HarmonyPatch(typeof(UIDeckCards), nameof(UIDeckCards.TurnOn))]
        internal static class DeckWindowLog
        {
            private static void Postfix(UIDeckCards __instance, int type, int cardQuantity, int cardTotal)
            {
                try
                {
                    if (!HeroSlots.Expanded || MatchManager.Instance == null)
                    {
                        return;
                    }
                    var mm = Traverse.Create(MatchManager.Instance);
                    int active = mm.Field("heroActive").GetValue<int>();
                    int waiting = MatchManager.Instance.heroIndexWaitingForAddDiscard;
                    var sb = new StringBuilder($"Deck window: type {type} (0 deck, 1 discard, 2 look/discard, 3 add, 4 echo), quantity {cardQuantity}, " +
                                               $"cards to show {cardTotal}, active hero {active}, looked-at hero {waiting}.");
                    Character[] heroes = mm.Field("TeamHero").GetValue<Character[]>();
                    if (heroes != null)
                    {
                        foreach (int i in new[] { active, waiting }.Distinct())
                        {
                            if (i >= 0 && i < heroes.Length && heroes[i]?.BattleCards != null)
                            {
                                Deck d = heroes[i].BattleCards;
                                sb.Append($"\n  hero {i + 1} {heroes[i].SourceName}: deck {d.Count()}, hand {d.Count(DeckType.Hand)}, discard {d.Count(DeckType.DeckDiscard)}, vanish {d.Count(DeckType.DeckVanish)}");
                            }
                        }
                    }
                    Plugin.Log.LogInfo(sb.ToString());
                    __instance.StartCoroutine(LogWindowLater(__instance));
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Deck window log failed: {e.Message}");
                }
            }
        }

        private static IEnumerator LogWindowLater(UIDeckCards window)
        {
            yield return new WaitForSeconds(1.5f);
            try
            {
                if (window == null || window.cardContainer == null)
                {
                    yield break;
                }
                var sb = new StringBuilder($"Deck window 1.5 s later: open {window.IsActive()}, text '{window.textInstructions?.text?.Replace("\n", " | ")}', " +
                                           $"{window.cardContainer.childCount} card object(s) in the container (world {window.cardContainer.position.x:0.00}, {window.cardContainer.position.y:0.00}).");
                for (int i = 0; i < window.cardContainer.childCount && i < 12; i++)
                {
                    Transform c = window.cardContainer.GetChild(i);
                    CardItem ci = c.GetComponent<CardItem>();
                    sb.Append($"\n  {c.name}: active {c.gameObject.activeInHierarchy}, card '{ci?.CardData?.Id}', world ({c.position.x:0.00}, {c.position.y:0.00}), scale {c.lossyScale.x:0.00}");
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Deck window log failed: {e.Message}");
            }
        }
    }
}
