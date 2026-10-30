using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using HarmonyLib;

namespace MoreHeroes
{
    // Party purchases in events: a group reply (no hero attached, not repeated per hero) that costs gold or shards and gives
    // something to every hero: perks (the alchemist brothers' transmutation, the special potions), healing (inns, healers, hot
    // springs), a card for each hero (learning a move) or an item. The price is set for 4 heroes, so with more heroes it scales
    // by heroes / 4 ([Balance] ScaleGroupEventCosts): 8 heroes pay twice. Found in the event data: 24 such replies, all of them
    // "pay for the whole group" offers. Paid replies that give one shared thing (loot, gold, travel, another event, a shop)
    // keep their price.
    // Every use of the price goes through the EventReplyData.GoldCost / DustCost getters (the affordability check when the
    // replies are drawn, the click check and the payment), so those are patched; the price written in the reply text is
    // replaced by the scaled one.
    internal static class EventCosts
    {
        private static readonly AccessTools.FieldRef<EventReplyData, int> GoldField = AccessTools.FieldRefAccess<EventReplyData, int>("goldCost");
        private static readonly AccessTools.FieldRef<EventReplyData, int> DustField = AccessTools.FieldRefAccess<EventReplyData, int>("dustCost");
        private static readonly HashSet<string> Logged = new HashSet<string>();

        private static bool Active()
        {
            return HeroSlots.Count > HeroSlots.VanillaCount && MultiplayerApi.ScaleGroupEventCosts && EventManager.Instance != null;
        }

        private static bool IsPartyPurchase(EventReplyData r)
        {
            if (r == null || (GoldField(r) <= 0 && DustField(r) <= 0))
            {
                return false;
            }
            if (r.RequiredClass != null || r.RepeatForAllCharacters || r.RepeatForAllWarriors || r.RepeatForAllScouts || r.RepeatForAllMages
                || r.RepeatForAllHealers)
            {
                return false;
            }
            return r.SsPerkData != null || r.SsPerkData1 != null || r.SsRewardHealthPercent > 0f || r.SsRewardHealthFlat > 0
                   || r.SsUpgradeRandomCard || r.SsAddItem != null || (r.SsAddCards != null && r.SsAddCards.Count > 0);
        }

        private static int Scale(int vanilla)
        {
            return (int)Math.Round(vanilla * (double)HeroSlots.Count / HeroSlots.VanillaCount, MidpointRounding.AwayFromZero);
        }

        private static void Apply(EventReplyData r, ref int cost, string what)
        {
            try
            {
                if (cost <= 0 || !Active() || !IsPartyPurchase(r))
                {
                    return;
                }
                int vanilla = cost;
                cost = Scale(vanilla);
                if (Logged.Add($"{r.ReplyText}/{what}/{HeroSlots.Count}"))
                {
                    Plugin.Log.LogInfo($"Event party purchase '{r.ReplyText}': {what} {vanilla} -> {cost} for {HeroSlots.Count} heroes ([Balance] ScaleGroupEventCosts).");
                }
            }
            catch (Exception e)
            {
                if (Logged.Add("error"))
                {
                    Plugin.Log.LogWarning($"Event cost scaling failed, vanilla price used: {e.Message}");
                }
            }
        }

        [HarmonyPatch(typeof(EventReplyData), nameof(EventReplyData.GoldCost), MethodType.Getter)]
        internal static class GoldCostPatch
        {
            private static void Postfix(EventReplyData __instance, ref int __result)
            {
                Apply(__instance, ref __result, "gold");
            }
        }

        [HarmonyPatch(typeof(EventReplyData), nameof(EventReplyData.DustCost), MethodType.Getter)]
        internal static class DustCostPatch
        {
            private static void Postfix(EventReplyData __instance, ref int __result)
            {
                Apply(__instance, ref __result, "shards");
            }
        }

        // The price is part of the translated reply text ("Pay 1000 shards ...", "for 1400g"). Replace the first number equal to
        // the vanilla price (written 1000, 1,000 or 1.000) with the scaled price in the same style.
        [HarmonyPatch(typeof(Reply), nameof(Reply.Init))]
        internal static class ReplyTextPatch
        {
            private static void Postfix(Reply __instance, string _eventId, int _replyIndex)
            {
                try
                {
                    if (!Active() || __instance.replyText == null)
                    {
                        return;
                    }
                    EventData ev = Globals.Instance.GetEventData(_eventId);
                    if (ev == null || ev.Replys == null || _replyIndex < 0 || _replyIndex >= ev.Replys.Length)
                    {
                        return;
                    }
                    EventReplyData r = ev.Replys[_replyIndex];
                    if (!IsPartyPurchase(r))
                    {
                        return;
                    }
                    int vanilla = GoldField(r) > 0 ? GoldField(r) : DustField(r);
                    string text = __instance.replyText.text;
                    string replaced = ReplaceNumber(text, vanilla, Scale(vanilla));
                    if (replaced != text)
                    {
                        __instance.replyText.text = replaced;
                    }
                    else if (Logged.Add("text " + _eventId + _replyIndex))
                    {
                        Plugin.Log.LogInfo($"Event party purchase: price {vanilla} not found in the reply text '{text}', text left as is.");
                    }
                }
                catch (Exception e)
                {
                    if (Logged.Add("text error"))
                    {
                        Plugin.Log.LogWarning($"Event reply price text failed: {e.Message}");
                    }
                }
            }
        }

        private static string ReplaceNumber(string text, int vanilla, int scaled)
        {
            foreach (string sep in new[] { ",", ".", " ", " ", "" })
            {
                string v = Group(vanilla, sep);
                Match m = Regex.Match(text, $@"(?<![\d.,]){Regex.Escape(v)}(?![\d])");
                if (m.Success)
                {
                    return text.Substring(0, m.Index) + Group(scaled, sep) + text.Substring(m.Index + m.Length);
                }
            }
            return text;
        }

        private static string Group(int n, string sep)
        {
            string plain = n.ToString(CultureInfo.InvariantCulture);
            if (sep == "" || plain.Length <= 3)
            {
                return plain;
            }
            return n.ToString("#,0", CultureInfo.InvariantCulture).Replace(",", sep);
        }
    }
}
