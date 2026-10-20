using System;
using System.Collections;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace MoreHeroes
{
    // End-of-run screen (FinishRun scene, reached by resigning, dying, or winning a run) with more than 4 heroes.
    // The scene has 4 hero panels under "Characters" (local scale 0.8 at y 1.5): charTL / charTR / charBL / charBR at local
    // (-5.25, -2.01), (6.55, -2.01), (-5.25, -4.85), (6.55, -4.85), each a portrait SpriteRenderer (spr0..spr3) with a
    // FinishProgression child (fp0..fp3: name, rank progress bar, perk points) on its outer side (x -2.2 left, +2.2 right).
    // Hero i uses panel i through GetSpriteRendererByPosition / GetFinishProgressionByPosition, which return null past 3, so
    // CalculateFinishRunReward threw a NullReferenceException on hero 5 and the screen hung on the loading screen.
    // With N > 4 heroes the Start prefix clones charBL (even heroes, left column) or charBR (odd heroes, right column) for heroes
    // 5..N, the getters return the clones, and all panels are laid out in N/2 rows in the space the 2 vanilla rows used.
    // Rank progress itself is granted by the vanilla loop over every hero (PlayerManager.ModifyProgress), so heroes 5..N get it
    // whatever happens to the panels. The run history entry (PlayerRun, runs.ato) keeps the vanilla 4 hero fields.
    internal static class FinishRunUi
    {
        private static readonly AccessTools.FieldRef<FinishRunManager, bool[]> LockedState =
            AccessTools.FieldRefAccess<FinishRunManager, bool[]>("lockedSate");

        private static SpriteRenderer[] portraits = new SpriteRenderer[0];
        private static FinishProgression[] progressions = new FinishProgression[0];
        private static int runHeroes;

        // Heroes in the run the screen reports on: the same team CalculateFinishRunReward reads (the backup team in zones that
        // swap the team on entrance). 0 in a 4-hero co-op room or when it cannot be read.
        private static int RunHeroes()
        {
            try
            {
                if (GameManager.Instance == null || (GameManager.Instance.IsMultiplayer() && !MultiplayerApi.Expanded) || AtOManager.Instance == null)
                {
                    return 0;
                }
                AtOManager ato = AtOManager.Instance;
                ZoneData zone = null;
                if (!string.IsNullOrEmpty(ato.currentMapNode))
                {
                    zone = Globals.Instance.GetNodeData(ato.currentMapNode)?.NodeZone;
                }
                Hero[] heroes = zone == null || zone.ChangeTeamOnEntrance ? ato.teamBackup?.ToArray() : ato.team?.ToArray();
                return heroes?.Length ?? 0;
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // Called from transpiled CalculateFinishRunReward: "heroes owned by this player == 4" picks the single-player gold/dust
        // formula; with more heroes it fell through to the per-player multiplayer formula (8 heroes: about 4x the gold and 3x the
        // dust). The 4 follows the run's team size so a single-player run keeps the vanilla payout.
        public static int Shift(int k)
        {
            return k + Math.Max(0, Math.Min(runHeroes, HeroSlots.MaxCount) - HeroSlots.VanillaCount);
        }

        [HarmonyPatch(typeof(FinishRunManager), "Start")]
        internal static class StartPatch
        {
            private static void Prefix(FinishRunManager __instance)
            {
                portraits = new SpriteRenderer[0];
                progressions = new FinishProgression[0];
                runHeroes = RunHeroes();
                if (runHeroes <= HeroSlots.VanillaCount)
                {
                    return;
                }
                Plugin.ReloadConfig();
                try
                {
                    AddPanels(__instance, Math.Min(runHeroes, HeroSlots.MaxCount));
                }
                catch (Exception e)
                {
                    portraits = new SpriteRenderer[0];
                    progressions = new FinishProgression[0];
                    Plugin.Log.LogError($"End of run: adding panels for heroes 5..{runHeroes} failed; they keep their rank progress but are not shown: {e}");
                }
            }
        }

        private static void AddPanels(FinishRunManager fr, int n)
        {
            SpriteRenderer[] vanilla = { fr.spr0, fr.spr1, fr.spr2, fr.spr3 };
            FinishProgression[] vanillaFp = { fr.fp0, fr.fp1, fr.fp2, fr.fp3 };
            if (vanilla.Any(s => s == null) || vanillaFp.Any(p => p == null))
            {
                throw new InvalidOperationException("the scene does not have the 4 expected hero panels");
            }
            var spr = new SpriteRenderer[n];
            var fp = new FinishProgression[n];
            Array.Copy(vanilla, spr, HeroSlots.VanillaCount);
            Array.Copy(vanillaFp, fp, HeroSlots.VanillaCount);
            for (int i = HeroSlots.VanillaCount; i < n; i++)
            {
                SpriteRenderer template = vanilla[i % 2 == 0 ? 2 : 3]; // bottom-left / bottom-right panel
                GameObject clone = UnityEngine.Object.Instantiate(template.gameObject, template.transform.parent);
                clone.name = $"char{i} MoreHeroes";
                spr[i] = clone.GetComponent<SpriteRenderer>();
                fp[i] = clone.GetComponentInChildren<FinishProgression>(true);
                if (spr[i] == null || fp[i] == null)
                {
                    throw new InvalidOperationException($"cloned panel for hero {i + 1} has no portrait or progress bar");
                }
            }

            bool[] locked = LockedState(fr);
            var grown = new bool[n];
            if (locked != null)
            {
                Array.Copy(locked, grown, Math.Min(locked.Length, n));
            }
            LockedState(fr) = grown;
            portraits = spr;
            progressions = fp;

            // Rows: heroes 1-2, 3-4, ... (left, right), like vanilla, with the vanilla spacing scaled by the panel size and the top
            // edge of the first row where vanilla has it. 0.2.5 test (8 heroes at 0.5): panels spanned world y -0.49..4.14 and left
            // the lower half of the screen empty; a panel reaches 1.56 world (1.95 parent-local) above its portrait's centre at
            // full size. 0.8 puts the last row's bottom at about y -3.0, above the main menu button's level.
            int rows = (n + 1) / 2;
            float topY = vanilla[0].transform.localPosition.y;
            float bottomY = vanilla[2].transform.localPosition.y;
            float vanillaPitch = topY - bottomY;
            float scale = Plugin.FinishRunScale.Value > 0.05f ? Plugin.FinishRunScale.Value : Math.Min(1f, 3.2f / rows);
            float pitch = vanillaPitch * scale;
            const float aboveCentre = 1.95f;
            float firstY = topY + aboveCentre * (1f - scale);
            float centre = firstY - (rows - 1) / 2f * pitch;
            Vector3 vanillaScale = vanilla[0].transform.localScale;
            var sb = new StringBuilder($"End of run: {n} hero panels in {rows} rows of 2, scale {scale:0.00} of vanilla, rows {pitch:0.00} apart (local; parent " +
                                       $"'{vanilla[0].transform.parent?.name}' scale {vanilla[0].transform.parent?.lossyScale.x:0.00}).");
            for (int i = 0; i < n; i++)
            {
                Transform t = spr[i].transform;
                float x = vanilla[i % 2].transform.localPosition.x;
                float y = centre + ((rows - 1) / 2f - i / 2) * pitch;
                t.localPosition = new Vector3(x, y, t.localPosition.z);
                t.localScale = new Vector3(vanillaScale.x * scale, vanillaScale.y * scale, vanillaScale.z);
                sb.Append($"\n  hero {i + 1} ({t.name}): local ({x:0.00}, {y:0.00}) world ({t.position.x:0.00}, {t.position.y:0.00})");
            }
            Plugin.Log.LogInfo(sb.ToString());
            fr.StartCoroutine(LogBoundsLater(fr));
        }

        private static IEnumerator LogBoundsLater(FinishRunManager fr)
        {
            yield return new WaitForSeconds(2f);
            try
            {
                if (fr == null)
                {
                    yield break;
                }
                var sb = new StringBuilder("End of run layout check (renderer bounds, world units; screen is about x -9.6..9.6, y -5.4..5.4 at 16:9):");
                for (int i = 0; i < portraits.Length; i++)
                {
                    Transform t = portraits[i] != null ? portraits[i].transform : null;
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
                if (fr.rewardsT != null)
                {
                    Bounds? rb = RewardsUi.VisibleBounds(fr.rewardsT);
                    if (rb != null)
                    {
                        sb.Append($"\n  score panel: x {rb.Value.min.x:0.00}..{rb.Value.max.x:0.00}, y {rb.Value.min.y:0.00}..{rb.Value.max.y:0.00}");
                    }
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"End of run layout check failed: {e.Message}");
            }
        }

        [HarmonyPatch(typeof(FinishRunManager), "GetSpriteRendererByPosition")]
        internal static class PortraitPatch
        {
            private static void Postfix(int position, ref SpriteRenderer __result)
            {
                if (__result == null && position >= 0 && position < portraits.Length)
                {
                    __result = portraits[position];
                }
            }
        }

        [HarmonyPatch(typeof(FinishRunManager), "GetFinishProgressionByPosition")]
        internal static class ProgressionPatch
        {
            private static void Postfix(int position, ref FinishProgression __result)
            {
                if (__result == null && position >= 0 && position < progressions.Length)
                {
                    __result = progressions[position];
                }
            }
        }

        // Safety net: if the panels could not be added, the per-hero loops still throw for hero 5. Rank progress for every hero
        // and the profile save happen before those loops; letting the screen carry on (main menu button) beats a hung loading
        // screen. The run history entry, saved at the end of the method, is lost in that case.
        [HarmonyPatch(typeof(FinishRunManager), "CalculateFinishRunReward")]
        internal static class CalculateGuard
        {
            private static Exception Finalizer(Exception __exception)
            {
                if (__exception == null || runHeroes <= HeroSlots.VanillaCount)
                {
                    return __exception;
                }
                Plugin.Log.LogError($"End of run with {runHeroes} heroes: the reward screen failed part-way ({__exception.GetType().Name}: {__exception.Message}); " +
                                    "continuing so the run can be closed. This run is probably missing from the Tome of Knowledge run history.\n" + __exception);
                return null;
            }
        }
    }
}
