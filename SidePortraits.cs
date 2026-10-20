using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace MoreHeroes
{
    // Side portraits (SideCharacters): the column of hero portraits on the left of the map, town, events and other out-of-combat
    // screens. Clicking one opens the character window (deck, items, level, perks) for that hero, so heroes 5..N could not be
    // inspected or managed outside combat. Every scene that has the column has its own root object "SideCharacters" with 4
    // children OverCharacter0..3 (inactive until Show), wired into charArray; Awake copies their transforms into charTransforms.
    // Resize (called by Show and by the map/town/challenge managers) puts the column at the top left and the portraits at local y
    // -1.24 * i: 4 portraits end at about y -0.8, 8 would run off the bottom of the screen.
    // With N > 4 heroes the Awake prefix clones OverCharacter3 for heroes 5..N and grows charArray and charTransforms. Nearly
    // every SideCharacters loop uses charArray.Length, so the clones are shown, refreshed and clicked like the vanilla ones. The
    // clones must be named OverCharacter4..N-1: the character window and challenge screen find portraits with
    // GameObject.Find("/SideCharacters/OverCharacter" + index). The Resize postfix scales the whole column (the hover and
    // character-screen animations only move the portraits' children, so they scale with it) so N portraits fit the screen.
    internal static class SidePortraits
    {
        private const float VanillaPitch = 1.24f;   // local distance between portraits (times Globals.multiplierY)
        private const float BottomMargin = 0.9f;    // world units kept free under the last portrait's centre
        private const float AutoMaxScale = 0.8f;
        private const float TrackGap = 0.3f;        // between the last portrait and the map's tracked-requirements list

        private static readonly AccessTools.FieldRef<SideCharacters, Transform[]> CharTransforms =
            AccessTools.FieldRefAccess<SideCharacters, Transform[]>("charTransforms");

        // Columns whose layout was already logged (Resize runs several times per scene).
        private static readonly HashSet<int> logged = new HashSet<int>();

        // Before Awake: it copies charArray[i].transform into charTransforms for every i, so both must be grown by then.
        [HarmonyPatch(typeof(SideCharacters), "Awake")]
        internal static class AwakePatch
        {
            private static void Prefix(SideCharacters __instance)
            {
                Grow(__instance, "Awake");
            }
        }

        // Safety net: a column created before the team was known (Awake saw 4 heroes) is grown when it is first shown.
        [HarmonyPatch(typeof(SideCharacters), nameof(SideCharacters.Show))]
        internal static class ShowPatch
        {
            private static void Prefix(SideCharacters __instance)
            {
                Grow(__instance, "Show");
            }
        }

        [HarmonyPatch(typeof(SideCharacters), nameof(SideCharacters.Resize))]
        internal static class ResizePatch
        {
            private static void Postfix(SideCharacters __instance)
            {
                try
                {
                    if (__instance.charArray == null || __instance.charArray.Length <= HeroSlots.VanillaCount)
                    {
                        return;
                    }
                    Layout(__instance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Side portraits: layout failed: {e}");
                }
            }
        }

        // The map's tracked-requirements list sits at the bottom left (x -7.6, from y -1.65 down), where the longer column now
        // reaches. MapManager.Resize places it after the column, so move it under the column here.
        [HarmonyPatch(typeof(MapManager), nameof(MapManager.Resize))]
        internal static class MapResizePatch
        {
            private static void Postfix(MapManager __instance)
            {
                try
                {
                    SideCharacters sc = __instance.sideCharacters;
                    Transform tracks = __instance.trackList;
                    if (sc == null || tracks == null || sc.charArray == null || sc.charArray.Length <= HeroSlots.VanillaCount)
                    {
                        return;
                    }
                    float bottom = ColumnBottom(sc);
                    float lowest = -Globals.Instance.sizeH * 0.5f + 0.6f;
                    float y = Mathf.Max(lowest, bottom - TrackGap);
                    if (tracks.position.y > y)
                    {
                        float was = tracks.position.y;
                        tracks.position = new Vector3(tracks.position.x, y, tracks.position.z);
                        Plugin.Log.LogInfo($"Side portraits: map track list moved from y {was:0.00} to {y:0.00} (under the portraits, which end at {bottom:0.00}).");
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Side portraits: moving the map track list failed: {e.Message}");
                }
            }
        }

        // The character window (deck, character sheet, level, items, perks) has its Exit button at the bottom left (local
        // (-sizeW/2 + 1, -2), set by CharacterWindowUI.Resize) and, on the deck tab, the energy cost graph and average under it.
        // Both sit over portraits 6..8 of the longer column. Mirror them to the right side of the screen (same height), where the
        // window has nothing below its tab buttons. Positions come from renderer bounds, so it does not matter how they are
        // anchored; only something still on the left half is moved, so repeated Show calls do not move it back. Resize puts the
        // Exit button back on the left, and the next Show moves it again. Not in combat: the column is hidden there.
        [HarmonyPatch(typeof(CharacterWindowUI), nameof(CharacterWindowUI.Show))]
        internal static class CharacterWindowPatch
        {
            private static void Postfix(CharacterWindowUI __instance)
            {
                try
                {
                    if (!HeroSlots.Expanded || MatchManager.Instance != null || !__instance.IsActive())
                    {
                        return;
                    }
                    MirrorRight(__instance.exitButton, "Exit button");
                    MirrorRight(__instance.deckEnergy != null ? __instance.deckEnergy.transform : null, "energy cost graph");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Side portraits: moving the character window's Exit button / energy graph failed: {e.Message}");
                }
            }
        }

        private static readonly HashSet<string> mirrorLogged = new HashSet<string>();

        private static void MirrorRight(Transform t, string what)
        {
            if (t == null || !t.gameObject.activeInHierarchy)
            {
                return;
            }
            Bounds? b = RewardsUi.VisibleBounds(t);
            float centreX = RewardsUi.ScreenWorld().x;
            if (b == null || b.Value.center.x >= centreX)
            {
                return;
            }
            // Mirrored, then pulled back inside the right edge: the vanilla Exit button sticks out past the left edge by design
            // (x -10.16..-7.06 at 16:9), which mirrored put "Exit" half off the screen.
            float dx = 2f * (centreX - b.Value.center.x);
            Vector4 screen = RewardsUi.ScreenWorld();
            dx -= Mathf.Max(0f, b.Value.max.x + dx - (screen.x + screen.z));
            t.position += new Vector3(dx, 0f, 0f);
            if (mirrorLogged.Add(what))
            {
                Plugin.Log.LogInfo($"Side portraits: character window {what} moved right by {dx:0.00} (was x {b.Value.min.x:0.00}..{b.Value.max.x:0.00}, " +
                                   $"y {b.Value.min.y:0.00}..{b.Value.max.y:0.00}).");
            }
        }

        private static void Grow(SideCharacters sc, string when)
        {
            int n = Math.Min(HeroSlots.Count, HeroSlots.MaxCount);
            OverCharacter[] arr = sc.charArray;
            if (n <= HeroSlots.VanillaCount || arr == null || arr.Length >= n)
            {
                return;
            }
            var created = new List<GameObject>();
            try
            {
                if (arr.Length != HeroSlots.VanillaCount || Array.Exists(arr, c => c == null))
                {
                    throw new InvalidOperationException($"expected 4 portraits in the scene, found {arr.Length}");
                }
                Transform parent = arr[0].transform.parent;
                if (parent != sc.transform || Array.Exists(arr, c => c.transform.parent != parent))
                {
                    throw new InvalidOperationException("the 4 portraits are not all children of SideCharacters");
                }

                // Build everything first, then assign.
                var all = new List<OverCharacter>(arr);
                OverCharacter template = arr[HeroSlots.VanillaCount - 1];
                for (int i = HeroSlots.VanillaCount; i < n; i++)
                {
                    GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, parent);
                    created.Add(clone);
                    clone.name = "OverCharacter" + i; // no suffix: found by name, see the class comment
                    OverCharacter oc = clone.GetComponent<OverCharacter>();
                    if (oc == null)
                    {
                        throw new InvalidOperationException($"cloned portrait for hero {i + 1} has no OverCharacter");
                    }
                    all.Add(oc);
                }
                var transforms = new Transform[all.Count];
                for (int i = 0; i < all.Count; i++)
                {
                    transforms[i] = all[i].transform;
                }
                sc.charArray = all.ToArray();
                CharTransforms(sc) = transforms;
                Plugin.Log.LogInfo($"Side portraits: added portraits for heroes 5..{n} in scene '{SceneManager.GetActiveScene().name}' ({when}).");
            }
            catch (Exception e)
            {
                foreach (GameObject go in created)
                {
                    UnityEngine.Object.Destroy(go);
                }
                sc.charArray = arr;
                Plugin.Log.LogError($"Side portraits: adding portraits for heroes 5..{n} failed; only heroes 1-4 have one: {e}");
            }
        }

        // Scale of the column: config, or as large as fits between the vanilla top portrait and the bottom of the screen (at most
        // 0.8, which leaves room under the column at 16:9).
        private static float Scale(SideCharacters sc, out string note)
        {
            int n = sc.charArray.Length;
            float configured = Plugin.SidePortraitScale.Value;
            if (configured > 0.05f)
            {
                note = "config";
                return configured;
            }
            float top = sc.transform.position.y;
            float bottom = -Globals.Instance.sizeH * 0.5f + BottomMargin;
            float fit = (top - bottom) / ((n - 1) * VanillaPitch * Globals.Instance.multiplierY);
            note = $"auto: fit {fit:0.00}";
            return Mathf.Clamp(Mathf.Min(AutoMaxScale, fit), 0.4f, 1f);
        }

        // World distance between portrait centres.
        private static float Pitch(SideCharacters sc)
        {
            float configured = Plugin.SidePortraitSpacing.Value;
            if (configured > 0.05f)
            {
                return configured;
            }
            return VanillaPitch * Globals.Instance.multiplierY * Mathf.Abs(sc.transform.lossyScale.y);
        }

        // World y of the lowest portrait's bottom edge (about 0.6 below its centre at full size).
        internal static float ColumnBottom(SideCharacters sc)
        {
            return sc.transform.position.y - (sc.charArray.Length - 1) * Pitch(sc) - 0.6f * Mathf.Abs(sc.transform.lossyScale.y);
        }

        // After vanilla Resize: the column's top-left anchor and the portraits' local positions are vanilla. Scale the column
        // around its anchor (portrait 1 stays where it was), and respace the portraits if a spacing is configured.
        private static void Layout(SideCharacters sc)
        {
            Plugin.ReloadConfig();
            float s = Scale(sc, out string note);
            Transform t = sc.transform;
            t.localScale = new Vector3(s, s, t.localScale.z);
            float pitch = Pitch(sc);
            if (Plugin.SidePortraitSpacing.Value > 0.05f)
            {
                Transform[] transforms = CharTransforms(sc);
                for (int i = 0; transforms != null && i < transforms.Length; i++)
                {
                    if (transforms[i] != null)
                    {
                        transforms[i].localPosition = new Vector3(0f, -i * pitch / s, 0f);
                    }
                }
            }

            int id = sc.GetInstanceID();
            if (!logged.Add(id))
            {
                return;
            }
            if (logged.Count > 64)
            {
                logged.Clear();
                logged.Add(id);
            }
            Plugin.Log.LogInfo($"Side portraits: {sc.charArray.Length} portraits in scene '{SceneManager.GetActiveScene().name}', scale {s:0.00} of vanilla ({note}), " +
                               $"{pitch:0.00} apart (world), top portrait at ({t.position.x:0.00}, {t.position.y:0.00}), column ends at about y {ColumnBottom(sc):0.00}.");
            if (sc.gameObject.activeInHierarchy)
            {
                sc.StartCoroutine(LogBoundsLater(sc));
            }
        }

        private static IEnumerator LogBoundsLater(SideCharacters sc)
        {
            yield return new WaitForSeconds(2f);
            try
            {
                if (sc == null || sc.charArray == null)
                {
                    yield break;
                }
                Vector4 screen = RewardsUi.ScreenWorld();
                var sb = new StringBuilder($"Side portraits layout check, scene '{SceneManager.GetActiveScene().name}' (renderer bounds, world units; screen " +
                                           $"x {screen.x - screen.z:0.00}..{screen.x + screen.z:0.00}, y {screen.y - screen.w:0.00}..{screen.y + screen.w:0.00}):");
                for (int i = 0; i < sc.charArray.Length; i++)
                {
                    OverCharacter oc = sc.charArray[i];
                    if (oc == null || !oc.gameObject.activeInHierarchy)
                    {
                        sb.Append($"\n  hero {i + 1}: not shown");
                        continue;
                    }
                    Bounds? b = RewardsUi.VisibleBounds(oc.transform);
                    string box = b == null
                        ? "no visible renderers"
                        : $"x {b.Value.min.x:0.00}..{b.Value.max.x:0.00}, y {b.Value.min.y:0.00}..{b.Value.max.y:0.00}";
                    BoxCollider2D col = oc.GetComponent<BoxCollider2D>();
                    string click = col != null && col.enabled
                        ? $", click area y {col.bounds.min.y:0.00}..{col.bounds.max.y:0.00}"
                        : ", not clickable";
                    sb.Append($"\n  hero {i + 1} ({oc.hero?.SourceName ?? "none"}): {box}{click}");
                }
                MapManager map = MapManager.Instance;
                Transform tracks = map != null && map.sideCharacters == sc ? map.trackList : null;
                if (tracks != null)
                {
                    Bounds? tb = RewardsUi.VisibleBounds(tracks);
                    sb.Append($"\n  map track list at ({tracks.position.x:0.00}, {tracks.position.y:0.00}), {tracks.childCount} track(s)" +
                              (tb != null ? $", x {tb.Value.min.x:0.00}..{tb.Value.max.x:0.00}, y {tb.Value.min.y:0.00}..{tb.Value.max.y:0.00}" : ""));
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Side portraits layout check failed: {e.Message}");
            }
        }
    }
}
