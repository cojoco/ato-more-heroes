using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace MoreHeroes
{
    // Loot screen (Loot scene, after a treasure node): each hero in turn picks one item or the gold pile. The scene has 4 hero
    // panels in one row under "Character" (world y 1.9): CharacterLoot0..3 at local x -6.6 / -2.2 / 2.2 / 6.6, hero 1 on the
    // left, each a portrait (CharacterImg, scale 0.6) with a row of 5 equipment circles at y 0 (x -1.55..1.65) and a deck button
    // at (1.67, 1.05). "Portraits/portrait0..3" are the small portraits put on a looted item (or under the gold button).
    // characterLootArray, characterPotraitArray and looted have 4 entries and every loop stops at 4, so heroes 5..N got no loot.
    // With N > 4 heroes the Start prefix clones a panel for each of heroes 5..N, grows the three arrays, and lays all panels out
    // in two rows (heroes 5..8 under 1..4; [Layout] LootRows 1 = one row across the screen) from the top of the screen down. The
    // item cards are dealt into cardContainer at local positions and local scale, so the container is scaled and moved to fit the
    // cards into the space left below the panels. Panel transforms themselves are left alone (they have an Animator): each panel
    // is moved into its own wrapper, which is placed and scaled.
    // The loops (IlPatches) follow the panels that exist (Shift), so if cloning fails the screen stays at 4.
    internal static class LootUi
    {
        // Measured in game (0.2.6 layout check, scaled back to full size): a panel spans x -2.28..2.28 and y -0.49..2.69 around
        // its centre; the hero picking grows to about 3.6 above it. Vanilla panels are 4.4 apart, centred at y 1.9, so their top
        // edge is at 4.59 (the subtitle "Character choosing" is centred at y 4.25, between the middle panels).
        // Item cards: with 4..10 items Functions.GetCardPosition deals 2 rows around (-2, -1.9); with 8 items they spanned
        // x -6.94..2.94, y -5.04..1.18 (container at the origin, scale 1). With 1..3 items there is one lower row.
        // The gold text ("You can choose one of the items or ...") starts at about y 0.1, right of the cards (x 4..8).
        private const float PanelHalfWidth = 2.28f;
        private const float PanelBelow = 0.49f;
        private const float PanelAbove = 2.69f;
        private const float VanillaPitchX = 4.4f;
        // Row pitch at full size. Renderer bounds say a panel reaches 2.69 above its centre, but on screen (0.2.8 screenshot, 8
        // heroes at 0.75) the tallest portraits only reach about 2.0 above it (full size) and the picking hero's portrait is 1.33x
        // bigger. 2.87 = 0.49 below + about 2.0 for a tall portrait + a little room for the enlarged one (was 3.33).
        private const float RowPitch = 2.87f;
        private const float RowsTopY = 4.59f;     // world y of the top row's top edge (vanilla's)
        private const float ScreenMargin = 0.3f;  // world units left free at each side
        private const float CardsTopLocal = 1.18f;
        private const float CardsBottomLocal = -5.04f;
        private const float CardsCentreXLocal = -2f;
        private const float CardsBottomY = -5.3f;  // world y the cards' bottom edge may reach (screen bottom -5.4)
        private const float CardsGap = 0.15f;      // between the lowest panels and the cards
        private const float GoldTextTopY = 0.1f;

        private static readonly AccessTools.FieldRef<LootManager, bool[]> Looted =
            AccessTools.FieldRefAccess<LootManager, bool[]>("looted");
        private static readonly AccessTools.FieldRef<LootManager, List<int>> CharacterOrder =
            AccessTools.FieldRefAccess<LootManager, List<int>>("characterOrder");
        private static readonly AccessTools.FieldRef<LootManager, int> ActiveCharacter =
            AccessTools.FieldRefAccess<LootManager, int>("activeCharacter");
        private static readonly AccessTools.FieldRef<LootManager, int> GoldSelected =
            AccessTools.FieldRefAccess<LootManager, int>("goldSelected");

        // Hero panels on the current loot screen (4 unless the extra panels were added).
        private static int slots = HeroSlots.VanillaCount;

        // Called from transpiled IL: k (a 4: loop bound, or the "no hero active" value of activeCharacter) becomes k + extra panels.
        public static int Shift(int k)
        {
            return k + slots - HeroSlots.VanillaCount;
        }

        [HarmonyPatch(typeof(LootManager), "Start")]
        internal static class StartPatch
        {
            // Before Start: its first loop hides every panel and clears looted[], so the arrays must be grown by then.
            private static void Prefix(LootManager __instance)
            {
                slots = HeroSlots.VanillaCount;
                int n = Math.Min(HeroSlots.Count, HeroSlots.MaxCount);
                if (n <= HeroSlots.VanillaCount)
                {
                    return;
                }
                Plugin.ReloadConfig();
                try
                {
                    AddPanels(__instance, n);
                }
                catch (Exception e)
                {
                    slots = HeroSlots.VanillaCount;
                    Plugin.Log.LogError($"Loot: adding panels for heroes 5..{n} failed; only heroes 1-4 get loot: {e}");
                }
            }
        }

        private static void AddPanels(LootManager lm, int n)
        {
            CharacterLoot[] panels = lm.characterLootArray;
            Transform[] portraits = lm.characterPotraitArray;
            bool[] looted = Looted(lm);
            if (panels == null || panels.Length != HeroSlots.VanillaCount || Array.Exists(panels, p => p == null)
                || portraits == null || portraits.Length != HeroSlots.VanillaCount || Array.Exists(portraits, p => p == null)
                || looted == null || looted.Length != HeroSlots.VanillaCount)
            {
                throw new InvalidOperationException($"expected 4 hero panels, 4 portraits and looted[4] in the scene, found {panels?.Length}, {portraits?.Length}, {looted?.Length}");
            }
            Transform row1 = panels[0].transform.parent;
            if (row1 == null || Array.Exists(panels, p => p.transform.parent != row1))
            {
                throw new InvalidOperationException("the 4 hero panels do not share one parent");
            }

            // Build everything first, then assign. Clones start as copies of the panel in the same vanilla column.
            var allPanels = new List<CharacterLoot>(panels);
            var allPortraits = new List<Transform>(portraits);
            var created = new List<GameObject>();
            try
            {
                for (int i = HeroSlots.VanillaCount; i < n; i++)
                {
                    CharacterLoot template = panels[i - HeroSlots.VanillaCount];
                    GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, row1);
                    created.Add(clone);
                    clone.name = $"CharacterLoot{i} MoreHeroes";
                    CharacterLoot cl = clone.GetComponent<CharacterLoot>();
                    if (cl == null)
                    {
                        throw new InvalidOperationException($"cloned panel for hero {i + 1} has no CharacterLoot");
                    }
                    allPanels.Add(cl);

                    Transform p = portraits[HeroSlots.VanillaCount - 1];
                    GameObject pClone = UnityEngine.Object.Instantiate(p.gameObject, p.parent);
                    created.Add(pClone);
                    pClone.name = $"portrait{i} MoreHeroes";
                    allPortraits.Add(pClone.transform);
                }
            }
            catch (Exception)
            {
                foreach (GameObject go in created)
                {
                    UnityEngine.Object.Destroy(go);
                }
                throw;
            }
            lm.characterLootArray = allPanels.ToArray();
            lm.characterPotraitArray = allPortraits.ToArray();
            var grown = new bool[n];
            Array.Copy(looted, grown, looted.Length);
            Looted(lm) = grown;
            slots = n;

            try
            {
                Layout(lm, allPanels, row1);
            }
            catch (Exception e)
            {
                // The panels work wherever they are; a failed layout only looks wrong.
                Plugin.Log.LogError($"Loot: laying out the {n} panels failed: {e}");
            }
            lm.StartCoroutine(LogBoundsLater(lm));
        }

        // Each panel goes into its own wrapper (same place and scale as the vanilla row parent, so nothing moves yet), then the
        // wrapper is moved so the panel lands in its slot. The panel's own transform keeps its vanilla local position and scale.
        private static void Layout(LootManager lm, List<CharacterLoot> panels, Transform row1)
        {
            int n = panels.Count;
            int rows = Mathf.Clamp(Plugin.LootRows.Value, 1, 2);
            int perRow = (n + rows - 1) / rows;
            Vector4 screen = RewardsUi.ScreenWorld();
            float parentScale = row1.lossyScale.x;
            if (Mathf.Abs(parentScale) < 0.01f)
            {
                parentScale = 1f;
            }

            // Panel scale: 0.75 for two rows (the cards shrink to make room), as large as the screen width allows for one row.
            float scale = Plugin.LootPanelScale.Value;
            string scaleNote = "config";
            if (scale <= 0.05f)
            {
                float widthFit = (2f * (screen.z - ScreenMargin)) / (VanillaPitchX * (perRow - 1) + 2f * PanelHalfWidth);
                scale = Mathf.Clamp(Mathf.Min(rows > 1 ? 0.75f : 1f, widthFit), 0.3f, 1f);
                scaleNote = $"auto: width fit {widthFit:0.00}";
            }
            float pitchX = VanillaPitchX * scale;
            float pitchY = Plugin.LootRowSpacing.Value > 0.05f ? Plugin.LootRowSpacing.Value : RowPitch * scale;
            float topCentreY = RowsTopY - PanelAbove * scale + Plugin.LootOffsetY.Value;
            float panelsBottom = topCentreY - (rows - 1) * pitchY - PanelBelow * scale;

            var sb = new StringBuilder($"Loot: {n} hero panels in {rows} row(s) of {perRow}, scale {scale:0.00} of vanilla ({scaleNote}), " +
                                       $"{pitchX:0.00} apart, rows {pitchY:0.00} apart, offset y {Plugin.LootOffsetY.Value:0.00} (world); panels end at y {panelsBottom:0.00}.");
            for (int i = 0; i < n; i++)
            {
                int r = i / perRow;
                int c = i % perRow;
                int inRow = Math.Min(perRow, n - r * perRow);
                float x = screen.x + (c - (inRow - 1) * 0.5f) * pitchX;
                float y = topCentreY - r * pitchY; // heroes 1..perRow in the top row

                Transform panel = panels[i].transform;
                var wrapper = new GameObject($"LootSlot{i} MoreHeroes").transform;
                wrapper.SetParent(row1.parent, false);
                wrapper.localPosition = row1.localPosition;
                wrapper.localRotation = row1.localRotation;
                wrapper.localScale = row1.localScale;
                panel.SetParent(wrapper, false);

                // Panel world position = wrapper position + panel local position x wrapper scale.
                wrapper.localScale = new Vector3(row1.localScale.x * scale, row1.localScale.y * scale, row1.localScale.z);
                Vector3 offset = Vector3.Scale(panel.localPosition, new Vector3(parentScale * scale, parentScale * scale, 0f));
                wrapper.position = new Vector3(x - offset.x, y - offset.y, wrapper.position.z);
                sb.Append($"\n  hero {i + 1}: centre ({panel.position.x:0.00}, {panel.position.y:0.00}), expected x {x - PanelHalfWidth * scale:0.00}..{x + PanelHalfWidth * scale:0.00}, " +
                          $"y {y - PanelBelow * scale:0.00}..{y + PanelAbove * scale:0.00}");
            }

            // Item cards: scale the container so the 2 rows of cards fit between the panels and the bottom of the screen, keeping
            // their centre column. Cards are dealt later (ShowItemsForLoot) at container-local positions, so they land in place.
            Transform cards = lm.cardContainer;
            if (cards != null)
            {
                float cardsTop = panelsBottom - CardsGap;
                float cardScale = Plugin.LootCardScale.Value;
                string cardNote = "config";
                if (cardScale <= 0.05f)
                {
                    cardScale = Mathf.Clamp((cardsTop - CardsBottomY) / (CardsTopLocal - CardsBottomLocal), 0.4f, 1f);
                    cardNote = "auto";
                }
                if (cardScale < 0.999f || Plugin.LootCardScale.Value > 0.05f)
                {
                    Vector3 cs = cards.localScale;
                    cards.localScale = new Vector3(cs.x * cardScale, cs.y * cardScale, cs.z);
                    float cardsX = CardsCentreXLocal - CardsCentreXLocal * cardScale;
                    cards.position = new Vector3(cardsX, cardsTop - CardsTopLocal * cardScale, cards.position.z);
                }
                sb.Append($"\n  item cards: scale {cardScale:0.00} of vanilla ({cardNote}), container at ({cards.position.x:0.00}, {cards.position.y:0.00}); " +
                          $"expected with 4-10 items x {CardsCentreXLocal - 4.94f * cardScale:0.00}..{CardsCentreXLocal + 4.94f * cardScale:0.00}, " +
                          $"y {cards.position.y + CardsBottomLocal * cardScale:0.00}..{cards.position.y + CardsTopLocal * cardScale:0.00}");
            }

            // The gold text and button sit right of the cards; move them down if the lowest panels reach them.
            float goldShift = Mathf.Max(0f, GoldTextTopY - (panelsBottom - CardsGap));
            if (goldShift > 0f)
            {
                foreach (Transform t in new[] { lm.description != null ? lm.description.transform : null, lm.botonGold != null ? lm.botonGold.transform : null })
                {
                    if (t != null)
                    {
                        t.position -= new Vector3(0f, goldShift, 0f);
                    }
                }
                sb.Append($"\n  gold text and button moved down {goldShift:0.00}");
            }
            Plugin.Log.LogInfo(sb.ToString());
        }

        // Heroes 5..N have no entry in the order list (single player builds [0, 1, 2, 3] in SetLoot).
        [HarmonyPatch(typeof(LootManager), "SetCharacters")]
        internal static class SetCharactersPatch
        {
            private static void Prefix(LootManager __instance)
            {
                try
                {
                    List<int> order = CharacterOrder(__instance);
                    if (slots <= HeroSlots.VanillaCount || order == null)
                    {
                        return;
                    }
                    for (int i = order.Count; i < slots; i++)
                    {
                        order.Add(i);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Loot: extending the hero order failed: {e.Message}");
                }
            }
        }

        // Each hero who takes the gold gets a small portrait under the gold button, 0.8 apart (room for 4). With more heroes
        // they are packed closer so 8 stay about as wide as vanilla's 4.
        [HarmonyPatch(typeof(LootManager), nameof(LootManager.LootGold))]
        internal static class GoldPortraitPatch
        {
            private static void Prefix(LootManager __instance, out int[] __state)
            {
                __state = new[] { ActiveCharacter(__instance), GoldSelected(__instance) };
            }

            private static void Postfix(LootManager __instance, int[] __state)
            {
                try
                {
                    int hero = __state[0];
                    int g = __state[1];
                    if (slots <= HeroSlots.VanillaCount || GoldSelected(__instance) != g + 1 || hero < 0 || hero >= slots
                        || __instance.botonGold == null)
                    {
                        return;
                    }
                    float spacing = 0.8f * HeroSlots.VanillaCount / slots;
                    Transform p = __instance.characterPotraitArray[hero];
                    p.position = __instance.botonGold.transform.position + new Vector3(-0.35f + spacing * g, -1f, 0f);
                    Plugin.Log.LogInfo($"Loot: hero {hero + 1} took the gold (pick {g + 1}), portrait at ({p.position.x:0.00}, {p.position.y:0.00}).");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Loot: placing the gold portrait failed: {e.Message}");
                }
            }
        }

        private static IEnumerator LogBoundsLater(LootManager lm)
        {
            yield return new WaitForSeconds(3f);
            try
            {
                if (lm == null || lm.characterLootArray == null)
                {
                    yield break;
                }
                Vector4 screen = RewardsUi.ScreenWorld();
                var sb = new StringBuilder($"Loot layout check (renderer bounds, world units; screen x {screen.x - screen.z:0.00}..{screen.x + screen.z:0.00}, " +
                                           $"y {screen.y - screen.w:0.00}..{screen.y + screen.w:0.00}):");
                for (int i = 0; i < lm.characterLootArray.Length; i++)
                {
                    Transform t = lm.characterLootArray[i] != null ? lm.characterLootArray[i].transform : null;
                    if (t == null || !t.gameObject.activeInHierarchy)
                    {
                        sb.Append($"\n  hero {i + 1}: not shown");
                        continue;
                    }
                    Bounds? b = RewardsUi.VisibleBounds(t);
                    sb.Append(b == null
                        ? $"\n  hero {i + 1}: no visible renderers"
                        : $"\n  hero {i + 1}: x {b.Value.min.x:0.00}..{b.Value.max.x:0.00}, y {b.Value.min.y:0.00}..{b.Value.max.y:0.00}");
                }
                if (lm.cardContainer != null)
                {
                    Bounds? cb = RewardsUi.VisibleBounds(lm.cardContainer);
                    if (cb != null)
                    {
                        sb.Append($"\n  item cards: x {cb.Value.min.x:0.00}..{cb.Value.max.x:0.00}, y {cb.Value.min.y:0.00}..{cb.Value.max.y:0.00}");
                    }
                }
                if (lm.botonGold != null)
                {
                    Bounds? gb = RewardsUi.VisibleBounds(lm.botonGold.transform);
                    Vector3 gp = lm.botonGold.transform.position;
                    sb.Append($"\n  gold button at ({gp.x:0.00}, {gp.y:0.00})" +
                              (gb != null ? $", x {gb.Value.min.x:0.00}..{gb.Value.max.x:0.00}, y {gb.Value.min.y:0.00}..{gb.Value.max.y:0.00}" : ""));
                }
                if (lm.description != null)
                {
                    Bounds? db = RewardsUi.VisibleBounds(lm.description.transform);
                    if (db != null)
                    {
                        sb.Append($"\n  gold text: x {db.Value.min.x:0.00}..{db.Value.max.x:0.00}, y {db.Value.min.y:0.00}..{db.Value.max.y:0.00}");
                    }
                }
                if (lm.subtitle != null)
                {
                    sb.Append($"\n  subtitle at y {lm.subtitle.transform.position.y:0.00}");
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Loot layout check failed: {e.Message}");
            }
        }
    }
}
