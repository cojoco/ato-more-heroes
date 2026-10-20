using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace MoreHeroes
{
    // "Combat stats" (damage meter): DamageMeterManager (scene 1, DontDestroyOnLoad, a uGUI canvas). Its rows are children of
    // Panel/Elements/Group, a GridLayoutGroup with one column of 960 x 140 cells: Header, CharacterMeter .. CharacterMeter (3),
    // Total. A row's portrait and texts sit 140 below the cell top (Content is centred in the row, the elements 70 under
    // Content's centre), so on screen the header is cell 0's top half and row i is centred on the line between cells i and i+1.
    // The background image (background-stats) has 5 lines drawn in: under the header and under each of the 4 rows.
    //
    // With more than 4 stat rows (AtOManager.combatStats has a row per hero, TeamPatches) the missing CharacterMeter rows are
    // cloned from row 4 and two heroes share each band between the drawn lines (heroes 1-2, 3-4, 5-6, 7-8): the cells become
    // 70 tall, the grid gets 140 top padding, Header and Total leave the grid (LayoutElement.ignoreLayout) and keep their
    // vanilla place, and the portrait and texts of every row are scaled about their own centres, so the columns still line up
    // with the header. The Tome of Knowledge shows past runs through the same screen with 4 rows: then everything goes back to
    // vanilla. The layout is redone every time the screen opens.
    internal static class DamageMeterUi
    {
        private const float BandCell = 70f;         // half of a vanilla cell: two rows per band
        private const float HeaderCell = 140f;      // vanilla cell height, kept as top padding for the header
        private const float DefaultPortraitScale = 0.52f; // portrait 120 tall -> 62 in a 70 cell
        private const float DefaultTextScale = 0.85f;     // value + "(total)" line, 70 tall -> 60

        private static readonly List<CharacterMeter> Rows = new List<CharacterMeter>(); // vanilla 4 first, then clones
        private static DamageMeterManager owner;
        private static GridLayoutGroup grid;
        private static Vector2 vanillaCell;
        private static int vanillaPadTop;
        private static RectTransform header;
        private static RectTransform total;
        private static Vector2 headerPos;
        private static Vector2 totalPos;
        private static int lastShown = -1;

        // Stat rows the screen can show: rows of combatStats (and combatStatsCurrent, which the loops also index), at most 8.
        private static int StatRows()
        {
            AtOManager ato = AtOManager.Instance;
            if (ato == null || ato.combatStats == null)
            {
                return HeroSlots.VanillaCount;
            }
            int rows = ato.combatStats.GetLength(0);
            if (ato.combatStatsCurrent != null)
            {
                rows = Math.Min(rows, ato.combatStatsCurrent.GetLength(0));
            }
            return Mathf.Clamp(rows, HeroSlots.VanillaCount, HeroSlots.MaxCount);
        }

        // Called from CharacterMeter.DoStats's IL: the totals / best-hero loop j < 4 becomes j < number of rows shown.
        public static int Shift(int k)
        {
            try
            {
                DamageMeterManager dm = DamageMeterManager.Instance;
                int rows = dm != null && dm.characterMeter != null ? Math.Min(dm.characterMeter.Length, StatRows()) : HeroSlots.VanillaCount;
                return k + Math.Max(0, rows - HeroSlots.VanillaCount);
            }
            catch (Exception)
            {
                return k;
            }
        }

        [HarmonyPatch(typeof(DamageMeterManager), nameof(DamageMeterManager.Show))]
        internal static class ShowPatch
        {
            private static void Prefix(DamageMeterManager __instance)
            {
                Plugin.ReloadConfig();
                try
                {
                    Prepare(__instance, StatRows());
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Combat stats: setting up rows failed, showing 4: {e}");
                    try
                    {
                        Prepare(__instance, HeroSlots.VanillaCount);
                    }
                    catch (Exception)
                    {
                    }
                }
            }
        }

        // Remember the vanilla screen the first time it is opened (the manager lives for the whole game).
        private static bool Capture(DamageMeterManager dm)
        {
            if (owner == dm && grid != null)
            {
                return true;
            }
            Rows.Clear();
            owner = null;
            grid = null;
            CharacterMeter[] meters = dm.characterMeter;
            if (meters == null || meters.Length != HeroSlots.VanillaCount || Array.Exists(meters, m => m == null))
            {
                Plugin.Log.LogError($"Combat stats: expected 4 CharacterMeter rows, found {meters?.Length ?? 0}; leaving the screen vanilla.");
                return false;
            }
            Transform parent = meters[0].transform.parent;
            GridLayoutGroup g = parent != null ? parent.GetComponent<GridLayoutGroup>() : null;
            if (g == null || Array.Exists(meters, m => m.transform.parent != parent))
            {
                Plugin.Log.LogError("Combat stats: the rows are not in one GridLayoutGroup; leaving the screen vanilla.");
                return false;
            }
            header = parent.Find("Header") as RectTransform;
            total = parent.Find("Total") as RectTransform;
            if (header == null || total == null)
            {
                Plugin.Log.LogError("Combat stats: Header or Total not found next to the rows; leaving the screen vanilla.");
                return false;
            }
            Rows.AddRange(meters);
            owner = dm;
            grid = g;
            vanillaCell = g.cellSize;
            vanillaPadTop = g.padding.top;
            headerPos = header.anchoredPosition;
            totalPos = total.anchoredPosition;
            return true;
        }

        private static void Prepare(DamageMeterManager dm, int n)
        {
            if (!Capture(dm))
            {
                return;
            }
            // Clone rows up to n (kept for later screens; the manager is never destroyed).
            Transform template = Rows[HeroSlots.VanillaCount - 1].transform;
            while (Rows.Count < n)
            {
                Transform prev = Rows[Rows.Count - 1].transform;
                GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.parent);
                clone.name = $"CharacterMeter ({Rows.Count}) MoreHeroes";
                clone.transform.SetSiblingIndex(prev.GetSiblingIndex() + 1);
                Rows.Add(clone.GetComponent<CharacterMeter>());
            }
            for (int i = HeroSlots.VanillaCount; i < Rows.Count; i++)
            {
                Rows[i].gameObject.SetActive(i < n);
            }
            var shown = new CharacterMeter[n];
            for (int i = 0; i < n; i++)
            {
                shown[i] = Rows[i];
            }
            dm.characterMeter = shown;

            bool banded = n > HeroSlots.VanillaCount;
            float portrait = banded ? (Plugin.MeterPortraitScale.Value > 0.05f ? Plugin.MeterPortraitScale.Value : DefaultPortraitScale) : 1f;
            float text = banded ? (Plugin.MeterTextScale.Value > 0.05f ? Plugin.MeterTextScale.Value : DefaultTextScale) : 1f;
            grid.cellSize = new Vector2(vanillaCell.x, banded ? BandCell : vanillaCell.y);
            grid.padding.top = banded ? vanillaPadTop + (int)HeaderCell : vanillaPadTop;
            Ignore(header, banded);
            Ignore(total, banded);
            header.anchoredPosition = headerPos;
            total.anchoredPosition = totalPos;
            for (int i = 0; i < n; i++)
            {
                ScaleRow(Rows[i], portrait, text);
            }
            LayoutRebuilder.MarkLayoutForRebuild(grid.transform as RectTransform);

            if (n != lastShown)
            {
                lastShown = n;
                Plugin.Log.LogInfo(banded
                    ? $"Combat stats: {n} rows, two per band between the background lines (cell {BandCell:0} tall, padding top {grid.padding.top}), " +
                      $"portrait scale {portrait:0.00}, text scale {text:0.00}."
                    : $"Combat stats: {n} rows, vanilla layout.");
                dm.StartCoroutine(LogLayoutLater(dm));
            }
        }

        private static void Ignore(RectTransform t, bool ignore)
        {
            LayoutElement le = t.GetComponent<LayoutElement>();
            if (le == null)
            {
                if (!ignore)
                {
                    return;
                }
                le = t.gameObject.AddComponent<LayoutElement>();
            }
            le.ignoreLayout = ignore;
        }

        // Portrait and texts scaled about their own centres; containers without a graphic (Detailed, Icons) pass the scale to
        // their children so their x positions stay put. The separators are inactive in vanilla and left alone.
        private static void ScaleRow(CharacterMeter row, float portrait, float text)
        {
            Transform content = row.content != null ? row.content : row.transform.Find("Content");
            if (content == null)
            {
                return;
            }
            Transform portraitT = row.image != null ? row.image.transform : null;
            foreach (Transform child in content)
            {
                if (child.name.StartsWith("Separator"))
                {
                    continue;
                }
                if (child == portraitT)
                {
                    child.localScale = new Vector3(portrait, portrait, 1f);
                }
                else if (child.GetComponent<Graphic>() == null && child.childCount > 0)
                {
                    foreach (Transform grand in child)
                    {
                        grand.localScale = new Vector3(text, text, 1f);
                    }
                }
                else
                {
                    child.localScale = new Vector3(text, text, 1f);
                }
            }
        }

        // After the screen has been laid out, log where each row's portrait and first value really are (world units), and the
        // totals, so a screenshot is not the only way to check overlaps.
        private static IEnumerator LogLayoutLater(DamageMeterManager dm)
        {
            yield return null;
            yield return null;
            try
            {
                if (dm == null || dm.characterMeter == null || !dm.IsActive())
                {
                    yield break;
                }
                var sb = new StringBuilder("Combat stats layout check (world units, centre y and height):");
                var corners = new Vector3[4];
                float prevBottom = float.MaxValue;
                for (int i = 0; i < dm.characterMeter.Length; i++)
                {
                    CharacterMeter m = dm.characterMeter[i];
                    if (m == null || m.image == null)
                    {
                        continue;
                    }
                    m.image.rectTransform.GetWorldCorners(corners);
                    float top = corners[1].y;
                    float bottom = corners[0].y;
                    sb.Append($"\n  row {i}: portrait y {(top + bottom) * 0.5f:0.00} ({top - bottom:0.00} tall), x {corners[0].x:0.00}..{corners[2].x:0.00}");
                    if (m.stats != null && m.stats.Length > 0 && m.stats[0] != null)
                    {
                        Bounds b = m.stats[0].bounds;
                        Vector3 c = m.stats[0].transform.TransformPoint(b.center);
                        sb.Append($", first value at ({c.x:0.00}, {c.y:0.00}) text '{m.stats[0].text.Replace("\n", " | ")}'");
                    }
                    if (top > prevBottom + 0.01f)
                    {
                        sb.Append("  (portrait overlaps the previous one)");
                    }
                    prevBottom = bottom;
                }
                if (dm.statsTotal != null && dm.statsTotal.Length > 0 && dm.statsTotal[0] != null)
                {
                    Vector3 tp = dm.statsTotal[0].transform.position;
                    sb.Append($"\n  totals: y {tp.y:0.00}, damage done total '{dm.statsTotal[0].text}'");
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Combat stats layout check failed: {e.Message}");
            }
        }
    }
}
