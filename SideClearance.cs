using System;
using System.Collections.Generic;
using UnityEngine;

namespace MoreHeroes
{
    // Buttons and panels that sit on the left of the screen, where the longer side portrait column (SidePortraits) now reaches
    // (8 portraits end at about y -3.95). Vanilla places them at the bottom left over portraits 6..8:
    // - the Exit button of the town buildings and event shops (CardCraftManager.exitCraftButton, local (-sizeW/2 + 1, -2.2),
    //   set by CardCraftManager.Resize; it sticks out past the left edge by design, x -10.16..-7.06);
    // - the deck energy graph and "Average energy cost" of the altar, church and forge (CardCraftManager.deckEnergy);
    // - the event book's Hide button (MapManager.eventShowHideButton, shown from the DoEventCo coroutine);
    // - the map corruption screen's Hide button (CorruptionHide, a child of MapManager.corruption, at (-9.7, -3.3) scale 1.1).
    // The craft screens are opened by an async method and the Hide button by a coroutine, so instead of patching each place that
    // shows them, this component checks a few times a second and moves whatever is showing to its spot (only when needed, so it
    // never fights the game):
    // - Exit and Hide go to the bottom left, under the portrait column (same x, bottom edge just above the screen bottom).
    // - The energy graph is mirrored to the right half in the altar, church and corruption screens (types 0, 1, 6, 7). The forge (type 2) has cards up
    //   to about x 8.2 there; the largest free area is the top right, between the "Search cards" box and the screen edge, under
    //   the top bar and above the first card row, so the graph is scaled to fit there (about 0.62), top-right aligned. In the forge's saved-decks
    //   panel (cardCraftSave) the graph is hidden; it is shown again when the panel closes.
    // The character window's own Exit button and energy graph are handled by SidePortraits.CharacterWindowPatch.
    internal class SideClearance : MonoBehaviour
    {
        private const float Interval = 0.25f;
        private const float BottomGap = 0.15f;     // between the bottom-left buttons and the screen bottom
        // Forge: free box at the top right (world units at 16:9, from a screenshot): right of the "Search cards" box (ends about
        // x 7.36), under the top bar (ends about y 4.78), above the first card row (starts about y 3.21).
        // 0.2.14: top 4.45 (the top labels touched the top bar at 4.7) and right edge 0.1 from the screen edge, the user's
        // request ("down and to the right a bit"); at the fit scale (about 0.62) the bottom-left corner reaches just past the
        // first card row's top-right corner, where the graph's own area is empty.
        private const float ForgeBoxLeft = 7.46f;
        private const float ForgeBoxTop = 4.45f;
        private const float ForgeBoxBottom = 3.01f;
        private const float ForgeBoxRightMargin = 0.1f;
        private const float EdgeMargin = 0.1f;

        private float next;
        private readonly HashSet<string> logged = new HashSet<string>();

        // The energy graph's vanilla placement, recorded the first time it is seen with sensible bounds, so every target is
        // computed from vanilla and switching between buildings (same object) does not compound moves.
        private DeckEnergy energy;
        private Vector3 energyPos;
        private Vector3 energyScale;
        private Bounds energyBounds;
        private DeckEnergy hiddenEnergy; // hidden by us in the forge's saved-decks panel

        private void Update()
        {
            if (Time.unscaledTime < next)
            {
                return;
            }
            next = Time.unscaledTime + Interval;
            try
            {
                if (!HeroSlots.Expanded || MatchManager.Instance != null)
                {
                    return;
                }
                SideCharacters sc = TownManager.Instance != null ? TownManager.Instance.sideCharacters
                    : MapManager.Instance != null ? MapManager.Instance.sideCharacters : null;
                if (sc == null || sc.charArray == null || sc.charArray.Length <= HeroSlots.VanillaCount)
                {
                    return;
                }
                float columnBottom = SidePortraits.ColumnBottom(sc);

                CardCraftManager cc = CardCraftManager.Instance;
                if (cc != null && cc.gameObject.activeInHierarchy)
                {
                    ToBottomLeft(cc.exitCraftButton, "craft screen Exit button", columnBottom);
                    PlaceEnergy(cc);
                }
                if (MapManager.Instance != null)
                {
                    ToBottomLeft(MapManager.Instance.eventShowHideButton, "event Hide button", columnBottom);
                    CorruptionManager corruption = MapManager.Instance.corruption;
                    if (corruption != null && corruption.gameObject.activeInHierarchy)
                    {
                        ToBottomLeft(corruption.transform.Find("CorruptionHide"), "corruption Hide button", columnBottom);
                    }
                }
            }
            catch (Exception e)
            {
                if (logged.Add("error"))
                {
                    Plugin.Log.LogWarning($"Side portraits: moving buttons out of the portrait column failed: {e}");
                }
            }
        }

        private void ToBottomLeft(Transform t, string what, float columnBottom)
        {
            if (t == null || !t.gameObject.activeInHierarchy)
            {
                return;
            }
            Bounds? b = RewardsUi.VisibleBounds(t);
            if (b == null)
            {
                return;
            }
            Vector4 screen = RewardsUi.ScreenWorld();
            float target = screen.y - screen.w + BottomGap;
            float dy = target - b.Value.min.y;
            if (Mathf.Abs(dy) < 0.02f)
            {
                return;
            }
            t.position += new Vector3(0f, dy, 0f);
            if (logged.Add(what))
            {
                float top = b.Value.max.y + dy;
                Plugin.Log.LogInfo($"Side portraits: {what} moved to the bottom left: y {b.Value.min.y:0.00}..{b.Value.max.y:0.00} -> {target:0.00}..{top:0.00}, " +
                                   $"x {b.Value.min.x:0.00}..{b.Value.max.x:0.00}; portraits end at {columnBottom:0.00}" +
                                   (top > columnBottom ? " (OVERLAPS the last portrait)" : "") + ".");
            }
        }

        // Craft types that show the energy graph: 0 altar, 1 church, 2 forge, 6 card corruption, 7 item corruption.
        private static bool ShowsEnergy(int craftType)
        {
            return craftType == 0 || craftType == 1 || craftType == 2 || craftType == 6 || craftType == 7;
        }

        private void PlaceEnergy(CardCraftManager cc)
        {
            DeckEnergy de = cc.deckEnergy;
            if (de == null)
            {
                return;
            }
            bool savePanel = cc.craftType == 2 && cc.cardCraftSave != null && cc.cardCraftSave.gameObject.activeInHierarchy;
            if (savePanel && de.gameObject.activeSelf)
            {
                de.gameObject.SetActive(false);
                hiddenEnergy = de;
                return;
            }
            if (!savePanel && hiddenEnergy == de)
            {
                hiddenEnergy = null;
                if (ShowsEnergy(cc.craftType))
                {
                    de.gameObject.SetActive(true);
                }
            }
            if (!de.gameObject.activeInHierarchy)
            {
                return;
            }
            Transform t = de.transform;
            if (energy != de)
            {
                // Record vanilla once; only trust bounds that look like the whole graph (text meshes may not exist on the very
                // first frame).
                Bounds? b0 = RewardsUi.VisibleBounds(t);
                if (b0 == null || b0.Value.size.x < 1f || b0.Value.size.y < 1f || b0.Value.center.x > RewardsUi.ScreenWorld().x)
                {
                    return;
                }
                energy = de;
                energyPos = t.position;
                energyScale = t.localScale;
                energyBounds = b0.Value;
            }

            Vector4 screen = RewardsUi.ScreenWorld();
            float right = screen.x + screen.z - EdgeMargin;
            float scale = 1f;
            Vector3 pos;
            string where;
            if (cc.craftType == 2)
            {
                // Forge: scale to fit the free box at the top right, top-right aligned. Measured afresh every check from where
                // the graph is now: the text meshes are not all built when the screen opens (0.2.13 recorded 1.93 tall instead
                // of 2.32, scaled to 0.75 and the top labels went under the top bar), so a one-off measurement is not enough.
                Bounds? cur = RewardsUi.VisibleBounds(t);
                if (cur == null || Mathf.Abs(energyScale.x) < 0.0001f)
                {
                    return;
                }
                float now = t.localScale.x / energyScale.x;
                Vector3 size = cur.Value.size / now; // vanilla-size bounds
                float boxRight = screen.x + screen.z - ForgeBoxRightMargin;
                scale = Mathf.Clamp(Mathf.Min((boxRight - ForgeBoxLeft) / size.x, (ForgeBoxTop - ForgeBoxBottom) / size.y), 0.3f, 1f);
                float k = scale / now;
                Vector3 p = t.position;
                Vector3 newMax = p + (cur.Value.max - p) * k;
                pos = p + new Vector3(boxRight - newMax.x, ForgeBoxTop - newMax.y, 0f);
                where = "forge top right";
            }
            else if (ShowsEnergy(cc.craftType))
            {
                // Altar, church, card/item corruption: mirror to the right half at the same height, kept inside the screen.
                float dx = 2f * (screen.x - energyBounds.center.x);
                dx -= Mathf.Max(0f, energyBounds.max.x + dx - right);
                pos = energyPos + new Vector3(dx, 0f, 0f);
                where = "right side";
            }
            else
            {
                return;
            }

            Vector3 wantScale = new Vector3(energyScale.x * scale, energyScale.y * scale, energyScale.z);
            if ((t.position - pos).sqrMagnitude < 0.0004f && (t.localScale - wantScale).sqrMagnitude < 0.0001f)
            {
                return;
            }
            t.localScale = wantScale;
            t.position = pos;
            if (logged.Add($"energy {cc.craftType} {scale:0.00}"))
            {
                Plugin.Log.LogInfo($"Side portraits: craft screen (type {cc.craftType}) energy graph moved to the {where}, scale {scale:0.00} of vanilla " +
                                   $"(now at ({pos.x:0.00}, {pos.y:0.00})), first seen " +
                                   $"from x {energyBounds.min.x:0.00}..{energyBounds.max.x:0.00}, y {energyBounds.min.y:0.00}..{energyBounds.max.y:0.00} " +
                                   $"by ({pos.x - energyPos.x:0.00}, {pos.y - energyPos.y:0.00}).");
            }
        }
    }
}
