using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace MoreHeroes
{
    // Event card minigames. CardPlayerManager (the "cards are shuffled, choose one" game) gives the picked card to every hero
    // (FinishSelectionCo loops over team.Size) and marks each hero with CardItem.ShowPortrait, but a CardItem has only 4 portrait
    // slots (SpriteRenderer[] portrait, a 2 x 2 grid under the card, or one row of 4 after AlignPortraitsHorizontally in the
    // town minigame) and ShowPortrait silently drops the 5th and later. With more than 4 heroes the slots are grown with clones
    // of the 4th and, once a 5th portrait shows, laid out from the vanilla slot positions:
    // - 2 x 2 grid: two rows of ceil(n / 2), scaled down (2.6 / columns) so 4 columns fit under the card;
    // - one row of 4 (town): further rows of 4 under it.
    // CardPlayerPairsManager shows one portrait per card (ShowPortrait(_, 1)) and never reaches the 5th slot.
    //
    // CardPlayerPairsManager (pairs memory game): 16 flips (maxRounds 8 rounds of 2), the flipping hero is
    // theTeam[orderArray[currentRound % 4]], orderArray (4 turn slots, padded with repeats when fewer heroes) is reshuffled every
    // 4 flips and shown by charSpr[4] (Selection/Char0..3: pairs of portraits, 0.9 apart in a pair, 2.1 from pair to pair). With
    // more than 4 heroes the turn slots become the team size rounded up to even (a pair of flips must not straddle a reshuffle:
    // the second flip looks up the slot before it), the "% 4" and slot loops follow PairsSlots (IlPatches specs), loops over
    // theTeam follow the team size, and charSpr is grown and shrunk to fit between its vanilla left edge and the screen edge.
    // Total flips stay 16, so every hero gets 2 flips with 8 heroes (same total rewards, spread over the team).
    internal static class MinigamesUi
    {
        private const float GridFit = 2.6f; // columns that fit at full size under a card, about (card width / slot spacing)

        private class Base
        {
            public Vector3[] Pos;
            public Vector3 Scale;
            public Vector3 ParentScale;
            public bool Logged;
        }

        private static readonly ConditionalWeakTable<CardItem, Base> Bases = new ConditionalWeakTable<CardItem, Base>();

        [HarmonyPatch(typeof(CardItem), nameof(CardItem.ShowPortrait))]
        internal static class ShowPortraitPatch
        {
            private static bool Prefix(CardItem __instance, Sprite _sprite, int _position)
            {
                try
                {
                    SpriteRenderer[] slots = __instance.portrait;
                    if (!HeroSlots.Expanded || slots == null || slots.Length < HeroSlots.VanillaCount || __instance.portraits == null)
                    {
                        return true;
                    }
                    int active = 0;
                    while (active < slots.Length && slots[active] != null && slots[active].gameObject.activeSelf)
                    {
                        active++;
                    }
                    if (active < HeroSlots.VanillaCount)
                    {
                        return true; // vanilla fills slots 1-4
                    }
                    if (active >= HeroSlots.Count)
                    {
                        return true; // nothing left to show (vanilla does nothing either)
                    }
                    slots = Grow(__instance, HeroSlots.Count);

                    // Same as vanilla for the 5th and later slots.
                    __instance.portraits.gameObject.SetActive(true);
                    if (_position == 1)
                    {
                        Transform p = __instance.portraits.transform;
                        p.localPosition = new Vector3(0.45f, 0.85f, p.localPosition.z);
                    }
                    slots[active].gameObject.SetActive(true);
                    slots[active].sprite = _sprite;
                    Layout(__instance, active + 1);
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Card minigame: showing a portrait for heroes 5..N failed: {e}");
                    return true;
                }
            }
        }

        private static SpriteRenderer[] Grow(CardItem card, int n)
        {
            SpriteRenderer[] slots = card.portrait;
            // Vanilla positions of slots 1-4, recorded when the 5th portrait is about to show (before Layout moves them). In the
            // town minigame AlignPortraitsHorizontally resets them to the same values before every call.
            if (!Bases.TryGetValue(card, out Base b))
            {
                b = new Base { Pos = new Vector3[HeroSlots.VanillaCount], Scale = slots[0].transform.localScale, ParentScale = card.portraits.localScale };
                for (int i = 0; i < HeroSlots.VanillaCount; i++)
                {
                    b.Pos[i] = slots[i].transform.localPosition;
                }
                Bases.Add(card, b);
            }
            if (slots.Length >= n)
            {
                return slots;
            }
            var list = new List<SpriteRenderer>(slots);
            SpriteRenderer template = slots[HeroSlots.VanillaCount - 1];
            while (list.Count < n)
            {
                GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
                clone.name = $"{template.gameObject.name} ({list.Count}) MoreHeroes";
                clone.SetActive(false);
                list.Add(clone.GetComponent<SpriteRenderer>());
            }
            card.portrait = list.ToArray();
            return card.portrait;
        }

        private static void Layout(CardItem card, int n)
        {
            if (!Bases.TryGetValue(card, out Base b))
            {
                return;
            }
            SpriteRenderer[] slots = card.portrait;
            Vector3[] p = b.Pos;
            bool oneRow = Mathf.Abs(p[0].y - p[2].y) < 0.01f;
            string how;
            if (oneRow)
            {
                // Town minigame: slots 1-4 in a row; more rows of 4 under it, a portrait height apart.
                float h = slots[0].sprite != null ? slots[0].sprite.bounds.size.y * Mathf.Abs(b.Scale.y) * 1.1f : Mathf.Abs(p[1].x - p[0].x);
                for (int i = 0; i < n; i++)
                {
                    Vector3 v = p[i % HeroSlots.VanillaCount];
                    slots[i].transform.localPosition = new Vector3(v.x, v.y - (i / HeroSlots.VanillaCount) * h, v.z);
                    slots[i].transform.localScale = b.Scale;
                }
                how = $"rows of 4, {h:0.00} apart";
            }
            else
            {
                // 2 x 2 grid (slots 1 2 / 3 4): two rows of ceil(n / 2), heroes 1-4 on top, scaled to fit under the card.
                // 0.2.20 scaled each slot, but the portraits kept their size on screen (something, probably their pop-in
                // animation, sets their scale), so 0.2.21 scales the shared parent and divides the slot positions by it.
                int cols = (n + 1) / 2;
                float s = Mathf.Min(1f, GridFit / cols);
                float dx = p[1].x - p[0].x;
                float dy = p[0].y - p[2].y;
                float cx = (p[0].x + p[1].x) * 0.5f;
                for (int i = 0; i < n; i++)
                {
                    int r = i / cols;
                    int c = i % cols;
                    float x = cx + (c - (cols - 1) * 0.5f) * dx * s;
                    float y = p[0].y - r * dy * s;
                    slots[i].transform.localPosition = new Vector3(x / s, y / s, p[0].z);
                    slots[i].transform.localScale = b.Scale;
                }
                card.portraits.localScale = new Vector3(b.ParentScale.x * s, b.ParentScale.y * s, b.ParentScale.z);
                how = $"2 rows of {cols}, parent scale {s:0.00}, slot spacing {dx:0.00} x {dy:0.00}; slot scale {slots[0].transform.localScale.x:0.00}";
            }
            if (!b.Logged && n >= HeroSlots.Count)
            {
                b.Logged = true;
                Plugin.Log.LogInfo($"Card minigame: {n} hero portraits on card '{card.CardData?.Id}' ({how}); vanilla slots at " +
                                   $"({p[0].x:0.00}, {p[0].y:0.00}) ({p[1].x:0.00}, {p[1].y:0.00}) ({p[2].x:0.00}, {p[2].y:0.00}) ({p[3].x:0.00}, {p[3].y:0.00}).");
            }
        }

        // Turn slots of the pairs game: 4 in vanilla, otherwise the team size rounded up to even.
        internal static int PairsSlots()
        {
            int n = HeroSlots.Count;
            return n <= HeroSlots.VanillaCount ? HeroSlots.VanillaCount : n + (n % 2);
        }

        // Called from CardPlayerPairsManager's IL: k (a 4: "% 4", turn slot loops, new int[4]) becomes the number of turn slots.
        public static int PairsShift(int k)
        {
            return k + PairsSlots() - HeroSlots.VanillaCount;
        }

        // Measured in game (0.2.22 layout check): a turn portrait is about 0.66 wide at the scene's 0.6 scale. The right panel
        // (text, turn strip, Finish button) spans about world x 3.65..9.0 at 16:9 (screenshot, 2026-10-20); keep a little inside.
        private const float TurnPortraitHalfWidth = 0.33f;
        private const float PanelRightMargin = 0.6f;
        private const float PanelLeftX = 3.85f;

        private static readonly AccessTools.FieldRef<CardPlayerPairsManager, Vector3> SpriteBig =
            AccessTools.FieldRefAccess<CardPlayerPairsManager, Vector3>("spriteBig");
        private static readonly AccessTools.FieldRef<CardPlayerPairsManager, Vector3> SpriteSmall =
            AccessTools.FieldRefAccess<CardPlayerPairsManager, Vector3>("spriteSmall");

        [HarmonyPatch(typeof(CardPlayerPairsManager), "Start")]
        internal static class PairsStartPatch
        {
            // Before Start: Start builds the turn order and shows it in charSpr (NextRound -> SetCharacterOrder).
            private static void Prefix(CardPlayerPairsManager __instance)
            {
                Plugin.ReloadConfig();
                try
                {
                    int slots = PairsSlots();
                    SpriteRenderer[] spr = __instance.charSpr;
                    if (slots <= HeroSlots.VanillaCount || spr == null || spr.Length != HeroSlots.VanillaCount || Array.Exists(spr, x => x == null))
                    {
                        if (slots > HeroSlots.VanillaCount)
                        {
                            Plugin.Log.LogError($"Card pairs: expected 4 turn portraits (charSpr), found {spr?.Length ?? 0}; the game may fail with {slots} turn slots.");
                        }
                        return;
                    }
                    LayoutTurns(__instance, slots);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Card pairs: adding turn portraits failed: {e}");
                }
            }
        }

        private static void LayoutTurns(CardPlayerPairsManager pm, int slots)
        {
            SpriteRenderer[] spr = pm.charSpr;
            Transform parent = spr[0].transform.parent;
            Vector3 c0 = spr[0].transform.localPosition;
            float dxIn = spr[1].transform.localPosition.x - c0.x;    // within a pair (0.9)
            float dxPair = spr[2].transform.localPosition.x - c0.x;  // pair to pair (2.1)
            var list = new List<SpriteRenderer>(spr);
            SpriteRenderer template = spr[HeroSlots.VanillaCount - 1];
            while (list.Count < slots)
            {
                GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
                clone.name = $"Char{list.Count} MoreHeroes";
                list.Add(clone.GetComponent<SpriteRenderer>());
            }

            // Centred where vanilla centres its 4 portraits (under the "Round / turn" text, world x 6.3), as wide as the right
            // panel allows (0.2.23; 0.2.22 kept Char0 in place and grew to the right, off centre). The current hero's portrait is
            // drawn at spriteBig (0.8 instead of 0.6), so leave room for a big end portrait.
            float small = SpriteSmall(pm).x;
            float big = SpriteBig(pm).x;
            float sceneScale = Mathf.Abs(spr[0].transform.localScale.x) > 0.01f ? Mathf.Abs(spr[0].transform.localScale.x) : 0.6f;
            float halfBig = TurnPortraitHalfWidth * big / sceneScale;
            float parentScale = Mathf.Abs(parent.lossyScale.x) > 0.01f ? Mathf.Abs(parent.lossyScale.x) : 1f;
            float vanillaCenter = c0.x + (dxPair + dxIn) * 0.5f;
            float halfSpan = ((slots / 2 - 1) * dxPair + dxIn) * 0.5f;
            float cw = parent.TransformPoint(new Vector3(vanillaCenter, c0.y, c0.z)).x;
            Vector4 screen = RewardsUi.ScreenWorld();
            float right = screen.x + screen.z - PanelRightMargin;
            float s = Plugin.PairsTurnScale.Value > 0.05f
                ? Plugin.PairsTurnScale.Value
                : Mathf.Clamp(Mathf.Min(right - cw, cw - PanelLeftX) / (halfSpan * parentScale + halfBig), 0.4f, 1f);

            for (int i = 0; i < list.Count; i++)
            {
                float x = vanillaCenter + s * ((i / 2) * dxPair + (i % 2) * dxIn - halfSpan);
                list[i].transform.localPosition = new Vector3(x, c0.y, list[i].transform.localPosition.z);
                list[i].transform.localScale = new Vector3(small * s, small * s, 1f);
            }
            SpriteSmall(pm) = new Vector3(small * s, small * s, 1f);
            SpriteBig(pm) = new Vector3(big * s, big * s, 1f);
            pm.charSpr = list.ToArray();
            Plugin.Log.LogInfo($"Card pairs: {slots} turn slots for {HeroSlots.Count} heroes, turn portraits scale {s:0.00} " +
                               $"(centred at world x {cw:0.00}, about {cw - s * (halfSpan * parentScale + halfBig):0.00}..{cw + s * (halfSpan * parentScale + halfBig):0.00}, panel {PanelLeftX:0.00}..{right:0.00}).");
            pm.StartCoroutine(LogTurnsLater(pm));
        }

        // Once the game has drawn the turn order and the "turn" text, log their bounds so overlaps show in the log.
        private static IEnumerator LogTurnsLater(CardPlayerPairsManager pm)
        {
            yield return new WaitForSeconds(3f);
            try
            {
                if (pm == null || pm.charSpr == null)
                {
                    yield break;
                }
                var sb = new StringBuilder("Card pairs layout check (renderer bounds, world units):");
                for (int i = 0; i < pm.charSpr.Length; i++)
                {
                    SpriteRenderer r = pm.charSpr[i];
                    if (r == null)
                    {
                        continue;
                    }
                    Bounds b = r.bounds;
                    sb.Append($"\n  turn {i}: x {b.min.x:0.00}..{b.max.x:0.00}, y {b.min.y:0.00}..{b.max.y:0.00}{(r.sprite == null ? " (no sprite)" : "")}");
                }
                Transform turnText = pm.charSpr[0].transform.parent.Find("PlayerTurn");
                Bounds? tb = turnText != null ? RewardsUi.VisibleBounds(turnText) : null;
                if (tb != null)
                {
                    sb.Append($"\n  turn text: x {tb.Value.min.x:0.00}..{tb.Value.max.x:0.00}, y {tb.Value.min.y:0.00}..{tb.Value.max.y:0.00}");
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Card pairs layout check failed: {e.Message}");
            }
        }
    }
}
