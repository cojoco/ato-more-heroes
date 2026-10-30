using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace MoreHeroes
{
    // Guards for moving heroes between boxes on the hero selection screen.
    //
    // HeroSelection.PickHero takes the box a hero is lifted from from HeroSelectionManager.GetOverBox (the box last hovered, set by
    // BoxSelection.OnMouseOver/OnMouseExit), not from the box the hero is in. With the extra row of smaller boxes that hover can
    // name the wrong box. Then the wrong box is emptied, the hero's own box is not, and dropping it on another box leaves the hero
    // in two boxes while the displaced portrait stays drawn where it was. BeginAdventure then throws in PerksManager.SetPlayerPerks
    // (duplicate key) and the run does not start (seen 2026-10-30 with ranger).
    internal static class HeroSelectGuards
    {
        private static readonly AccessTools.FieldRef<HeroSelectionManager, GameObject> OverBox =
            AccessTools.FieldRefAccess<HeroSelectionManager, GameObject>("box");

        private static readonly AccessTools.FieldRef<HeroSelectionManager, Dictionary<GameObject, HeroSelection>> BoxHero =
            AccessTools.FieldRefAccess<HeroSelectionManager, Dictionary<GameObject, HeroSelection>>("boxHero");

        // The box that holds this hero, or null when it is in the roster. With duplicates, the box its portrait is drawn on.
        private static GameObject BoxOf(HeroSelectionManager hsm, HeroSelection hs)
        {
            Dictionary<GameObject, HeroSelection> boxHero = BoxHero(hsm);
            if (boxHero == null || hs == null)
            {
                return null;
            }
            return boxHero.Where(kv => kv.Key != null && kv.Value == hs)
                          .Select(kv => kv.Key)
                          .OrderBy(b => hs.sprite != null ? Vector2.Distance(b.transform.position, hs.sprite.position) : 0f)
                          .FirstOrDefault();
        }

        // Point the hover at the hero's real box while PickHero runs, then restore the hover for the drop.
        [HarmonyPatch(typeof(HeroSelection), nameof(HeroSelection.PickHero))]
        internal static class PickHeroPatch
        {
            private static void Prefix(HeroSelection __instance, bool _comingFromRandom, out GameObject __state)
            {
                __state = null;
                try
                {
                    HeroSelectionManager hsm = HeroSelectionManager.Instance;
                    if (hsm == null || _comingFromRandom)
                    {
                        return;
                    }
                    __state = OverBox(hsm);
                    GameObject real = BoxOf(hsm, __instance);
                    if (real != __state)
                    {
                        Plugin.Log.LogInfo($"Hero selection: {__instance.Id} picked up from {(real != null ? real.name : "the roster")}, " +
                                           $"hover said {(__state != null ? __state.name : "none")}; using {(real != null ? real.name : "none")}.");
                    }
                    OverBox(hsm) = real;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Hero selection: pick-up box check failed: {e.Message}");
                }
            }

            private static void Postfix(bool _comingFromRandom, GameObject __state)
            {
                HeroSelectionManager hsm = HeroSelectionManager.Instance;
                if (hsm != null && !_comingFromRandom)
                {
                    OverBox(hsm) = __state;
                }
            }
        }

        // On a mouse drop, set the hover to the box art under the pointer before PickStop reads it: the hover field can still name
        // the box above (see HeroSelectUi.ClipBoxColliders). Patched here rather than on GetOverBox, which is a one-line getter the
        // runtime may inline. Controller selection (controllerCurrentHS, or an explicit box index) moves the hover itself.
        [HarmonyPatch(typeof(HeroSelection), nameof(HeroSelection.PickStop))]
        internal static class DropPatch
        {
            private static void Prefix(HeroSelection __instance, int _box, bool _removeHeroAbsolute)
            {
                try
                {
                    HeroSelectionManager hsm = HeroSelectionManager.Instance;
                    if (hsm == null || _box != -1 || _removeHeroAbsolute || !hsm.dragging || hsm.controllerCurrentHS != null ||
                        HeroSelectUi.Extra() == 0 || hsm.boxGO == null)
                    {
                        return;
                    }
                    Vector3 p = GameManager.Instance.cameraMain.ScreenToWorldPoint(Input.mousePosition);
                    GameObject hit = null;
                    foreach (GameObject b in hsm.boxGO)
                    {
                        Bounds? v = b != null && b.activeInHierarchy ? RewardsUi.VisibleBounds(b.transform) : null;
                        if (v != null && p.x >= v.Value.min.x && p.x <= v.Value.max.x && p.y >= v.Value.min.y && p.y <= v.Value.max.y)
                        {
                            hit = b;
                            break;
                        }
                    }
                    GameObject hover = OverBox(hsm);
                    if (hit != hover)
                    {
                        Plugin.Log.LogInfo($"Hero selection: {__instance.Id} dropped on {(hit != null ? hit.name : "no box")} at ({p.x:0.00}, {p.y:0.00}), " +
                                           $"hover said {(hover != null ? hover.name : "none")}; using {(hit != null ? hit.name : "none")}.");
                    }
                    OverBox(hsm) = hit;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Hero selection: drop box check failed: {e.Message}");
                }
            }
        }

        // Safety net: if a hero is still in two boxes (or a drawn portrait is in no box), repair the boxes from where the portraits
        // are drawn, tell the player, and keep the screen open so they can check the team and press Begin again.
        [HarmonyPatch(typeof(HeroSelectionManager), nameof(HeroSelectionManager.BeginAdventure))]
        internal static class BeginAdventureCheck
        {
            [HarmonyPriority(Priority.First)]
            private static bool Prefix(HeroSelectionManager __instance)
            {
                try
                {
                    if (!GameManager.Instance.IsMainPlayer() || GameManager.Instance.GameStatus == Enums.GameStatus.LoadGame)
                    {
                        return true;
                    }
                    List<string> fixes = Repair(__instance);
                    if (fixes.Count == 0)
                    {
                        return true;
                    }
                    Plugin.Log.LogWarning($"Hero selection: Begin Adventure stopped, the boxes did not match the portraits. Repaired: {string.Join("; ", fixes)}.");
                    __instance.botonBegin.gameObject.SetActive(true);
                    AlertManager.Instance.AlertConfirm("A hero was in two boxes at once (a swap went wrong).\n\n" +
                                                       "The boxes have been reset to the portraits shown. Check your team, then press Begin Adventure again.");
                    return false;
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Hero selection: duplicate hero check failed, starting anyway: {e}");
                    return true;
                }
            }
        }

        private static List<string> Repair(HeroSelectionManager hsm)
        {
            var fixes = new List<string>();
            Dictionary<GameObject, HeroSelection> boxHero = BoxHero(hsm);
            GameObject[] boxes = hsm.boxGO;
            if (boxHero == null || boxes == null)
            {
                return fixes;
            }

            // A hero in several boxes stays in the box its portrait is drawn on; the other boxes are emptied.
            foreach (IGrouping<HeroSelection, GameObject> g in boxes.Where(b => b != null && boxHero.TryGetValue(b, out HeroSelection h) && h != null)
                                                                    .GroupBy(b => boxHero[b]))
            {
                if (g.Count() < 2)
                {
                    continue;
                }
                GameObject keep = BoxOf(hsm, g.Key);
                foreach (GameObject b in g.Where(b => b != keep))
                {
                    hsm.FillBox(b, null, _state: false);
                    fixes.Add($"{g.Key.Id} removed from {b.name} (kept in {keep.name})");
                }
            }

            // A picked hero in no box: put it in the empty box its portrait is drawn on, or send it back to the roster.
            var inBoxes = new HashSet<HeroSelection>(boxHero.Values.Where(h => h != null));
            foreach (HeroSelection hs in hsm.heroSelectionDictionary.Values)
            {
                if (hs == null || !hs.HeroPicked || inBoxes.Contains(hs) || hs.sprite == null)
                {
                    continue;
                }
                GameObject empty = boxes.Where(b => b != null && b.activeInHierarchy && (!boxHero.TryGetValue(b, out HeroSelection h) || h == null) &&
                                                    hsm.IsYourBox(b.name))
                                        .OrderBy(b => Vector2.Distance(b.transform.position, hs.sprite.position))
                                        .FirstOrDefault();
                if (empty != null && Vector2.Distance(empty.transform.position, hs.sprite.position) < 0.5f)
                {
                    hsm.FillBox(empty, hs, _state: true);
                    inBoxes.Add(hs);
                    fixes.Add($"{hs.Id} put in {empty.name}, where its portrait was");
                }
                else
                {
                    hs.GoBackToOri();
                    fixes.Add($"{hs.Id} sent back to the roster");
                }
            }

            if (fixes.Count > 0)
            {
                // Redraw every boxed portrait on its box.
                float PortraitScale(HeroSelection h) => GameManager.Instance.IsMultiplayer() ? h.selectedPortraitScaleMultiplayer : h.selectedPortraitScale;
                foreach (KeyValuePair<GameObject, HeroSelection> kv in boxHero.ToList())
                {
                    if (kv.Key != null && kv.Value != null && kv.Value.sprite != null)
                    {
                        kv.Value.sprite.position = kv.Key.transform.position;
                        kv.Value.sprite.localScale = Vector3.one * PortraitScale(kv.Value);
                    }
                }
                hsm.ShowHeroesByFilterAsync(hsm.CurrentFilter);
            }
            return fixes;
        }
    }
}
