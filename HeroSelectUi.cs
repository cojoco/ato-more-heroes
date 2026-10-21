using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;

namespace MoreHeroes
{
    // Hero selection with a box per hero. The scene has 4 boxes (RightBlock/BoxCharacters/Boxes/Box_0..Box_3, right to left,
    // Box_0 is the front hero). In single-player Adventure with HeroCount > 4 the mod clones Box_0.. into Box_4.. and places them
    // in a second row under the first (that space only holds multiplayer widgets). Box ids come from the GameObject name
    // ("Box_N" is parsed by BoxSelection.Start, FillBox and IsYourBox), and boxGO[i] is box i, so the clones are appended in order.
    // The clones are made in a Prefix on HeroSelectionManager.Start, before StartCoContinue builds boxSelection/boxHero/boxFilled
    // from boxGO, so the game's own code treats them like the scene boxes. The box loops that were hardcoded to 4 are transpiled
    // (IlPatches, HeroSelectUi.Shift). Every other mode (weekly, Obelisk challenge, Singularity, tutorial, combat tool, loading
    // a single-player game) keeps 4 boxes, and then Shift returns k unchanged. Co-op Adventure gets the room's hero count
    // (MultiplayerApi, set by the MorePlayers mod), 4 without it.
    internal static class HeroSelectUi
    {
        // Extra boxes on the current hero selection screen (boxGO.Length - 4). 0 when the screen is vanilla or gone.
        internal static int Extra()
        {
            try
            {
                HeroSelectionManager hsm = HeroSelectionManager.Instance;
                if (hsm == null || hsm.boxGO == null)
                {
                    return 0;
                }
                return Math.Max(0, Math.Min(hsm.boxGO.Length, HeroSlots.MaxCount) - HeroSlots.VanillaCount);
            }
            catch (Exception)
            {
                return 0;
            }
        }

        // Called from transpiled IL: k (a 4) becomes k + extra boxes.
        public static int Shift(int k)
        {
            return k + Extra();
        }

        internal static string SkipReason()
        {
            GameManager gm = GameManager.Instance;
            AtOManager ato = AtOManager.Instance;
            if (gm == null || ato == null)
            {
                return "game not ready";
            }
            // In co-op the host's room decides (MorePlayers), so every player gets the same boxes whatever their own config says.
            if (!Plugin.Enabled.Value && !gm.IsMultiplayer())
            {
                return "[General] Enabled = false";
            }
            if (gm.IsMultiplayer())
            {
                if (!MultiplayerApi.Expanded)
                {
                    return "multiplayer room with 4 heroes (more need the MorePlayers mod on every player's game)";
                }
            }
            else if (Plugin.TargetHeroCount <= HeroSlots.VanillaCount)
            {
                return "[General] HeroCount = 4";
            }
            if (!gm.IsGameAdventure() || gm.IsWeeklyChallenge() || gm.IsSingularity() || gm.IsObeliskChallenge())
            {
                return $"only Adventure mode gets extra heroes (game type {gm.GameType})";
            }
            if (gm.IsTutorialGame() || ato.IsFirstGame())
            {
                return "tutorial run";
            }
            if (ato.IsCombatTool)
            {
                return "combat tool";
            }
            // A co-op save is loaded through this screen (players are given the saved heroes' boxes), so it needs a box per saved hero.
            if (gm.IsLoadingGame() && !gm.IsMultiplayer())
            {
                return "loading a saved game";
            }
            return null;
        }

        // Heroes the player can actually put in a box: main characters that are unlocked and whose DLC is owned.
        private static int SelectableHeroes()
        {
            int n = 0;
            foreach (KeyValuePair<string, SubClassData> kv in Globals.Instance.SubClass)
            {
                SubClassData scd = kv.Value;
                if (scd == null || !scd.MainCharacter || !PlayerManager.Instance.IsHeroUnlocked(scd.Id))
                {
                    continue;
                }
                try
                {
                    if (!string.IsNullOrEmpty(scd.Sku) && !SteamManager.Instance.PlayerHaveDLC(scd.Sku))
                    {
                        continue;
                    }
                }
                catch (Exception)
                {
                    continue;
                }
                n++;
            }
            return n;
        }

        [HarmonyPatch(typeof(HeroSelectionManager), "Start")]
        internal static class StartPatch
        {
            private static void Prefix(HeroSelectionManager __instance)
            {
                Plugin.ReloadConfig();
                try
                {
                    AddBoxes(__instance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Hero selection: adding boxes failed, you pick 4 and the rest are added from [Spike] ExtraHeroes: {e}");
                }
            }
        }

        private static void AddBoxes(HeroSelectionManager hsm)
        {
            string reason = SkipReason();
            if (reason != null)
            {
                Plugin.Log.LogInfo($"Hero selection: vanilla 4 boxes ({reason}).");
                return;
            }
            GameObject[] boxes = hsm.boxGO;
            if (boxes == null || boxes.Length != HeroSlots.VanillaCount)
            {
                Plugin.Log.LogError($"Hero selection: expected 4 boxes in the scene, found {boxes?.Length ?? 0}; leaving it vanilla.");
                return;
            }
            for (int i = 0; i < boxes.Length; i++)
            {
                if (boxes[i] == null || boxes[i].name != "Box_" + i || boxes[i].GetComponent<BoxSelection>() == null)
                {
                    Plugin.Log.LogError($"Hero selection: box {i} is '{boxes[i]?.name}', expected 'Box_{i}' with a BoxSelection; leaving it vanilla.");
                    return;
                }
            }

            // Co-op: the room's hero count, the same on every client (box ids and the box loops must match between players), and
            // not limited by this profile's unlocked heroes (each player picks from their own).
            bool coop = GameManager.Instance.IsMultiplayer();
            int target = coop ? MultiplayerApi.Current() : Plugin.TargetHeroCount;
            int selectable = coop ? target : SelectableHeroes();
            int n = Math.Min(target, selectable);
            if (n < target)
            {
                Plugin.Log.LogWarning($"Hero selection: HeroCount is {target} but only {selectable} heroes are unlocked and owned, so the screen " +
                                      $"shows {Math.Max(n, HeroSlots.VanillaCount)} boxes; the rest are added from [Spike] ExtraHeroes when the run begins.");
            }
            if (n <= HeroSlots.VanillaCount)
            {
                return;
            }

            float dx = Plugin.SelectionRowOffsetX.Value;
            float dy = Plugin.SelectionRowOffsetY.Value;
            var all = new List<GameObject>(boxes);
            for (int i = HeroSlots.VanillaCount; i < n; i++)
            {
                // Box_4 goes under Box_0, Box_5 under Box_1, ... (same parent, same scale).
                GameObject template = boxes[i - HeroSlots.VanillaCount];
                GameObject clone = UnityEngine.Object.Instantiate(template, template.transform.parent);
                clone.name = "Box_" + i; // must be renamed before BoxSelection.Start parses it (next frame)
                clone.transform.position = template.transform.position + new Vector3(dx, -dy, 0f);
                all.Add(clone);
            }
            hsm.boxGO = all.ToArray();
            LogLayout(hsm, "opened");
        }

        private static readonly AccessTools.FieldRef<HeroSelectionManager, Dictionary<GameObject, HeroSelection>> BoxHero =
            AccessTools.FieldRefAccess<HeroSelectionManager, Dictionary<GameObject, HeroSelection>>("boxHero");

        // Unscaled values, so applying the layout twice does not shrink things twice.
        private static readonly Dictionary<Transform, Vector3> BaseBoxScale = new Dictionary<Transform, Vector3>();
        private static readonly Dictionary<HeroSelection, float> BasePortraitScale = new Dictionary<HeroSelection, float>();

        // Once the screen has settled, shrink all boxes and stack the two rows in the space between the header text and the
        // hero roster (measured in game: header bottom about y 3.6, roster top about y -1.5; at full size the second row ran into
        // the roster). Rows keep the game's x positions. A picked hero's portrait is placed at its box's position with the fixed
        // size HeroSelection.selectedPortraitScale (1.3), so that field is scaled too and heroes already in boxes are re-snapped.
        internal static void ApplyLayout(HeroSelectionManager hsm)
        {
            try
            {
                GameObject[] boxes = hsm.boxGO;
                float s = Plugin.SelectionBoxScale.Value;
                if (boxes == null || boxes.Length <= HeroSlots.VanillaCount || s <= 0.05f)
                {
                    return;
                }
                foreach (GameObject b in boxes)
                {
                    if (b == null)
                    {
                        continue;
                    }
                    if (!BaseBoxScale.TryGetValue(b.transform, out Vector3 baseScale))
                    {
                        baseScale = b.transform.localScale;
                        BaseBoxScale[b.transform] = baseScale;
                    }
                    b.transform.localScale = new Vector3(baseScale.x * s, baseScale.y * s, baseScale.z);
                }

                // Row tops: row 1 at SelectionTopY, row 2 one box height plus SelectionRowGap lower.
                Bounds? first = RewardsUi.VisibleBounds(boxes[0].transform);
                float boxHeight = first?.size.y ?? 0f;
                for (int i = 0; i < boxes.Length; i++)
                {
                    GameObject b = boxes[i];
                    Bounds? v = b != null ? RewardsUi.VisibleBounds(b.transform) : null;
                    if (v == null)
                    {
                        continue;
                    }
                    int row = i / HeroSlots.VanillaCount;
                    float top = Plugin.SelectionTopY.Value - row * (boxHeight + Plugin.SelectionRowGap.Value);
                    b.transform.position += new Vector3(0f, top - v.Value.max.y, 0f);
                }

                foreach (HeroSelection hs in hsm.heroSelectionDictionary.Values)
                {
                    if (hs == null)
                    {
                        continue;
                    }
                    if (!BasePortraitScale.TryGetValue(hs, out float basePortrait))
                    {
                        basePortrait = hs.selectedPortraitScale;
                        BasePortraitScale[hs] = basePortrait;
                    }
                    hs.selectedPortraitScale = basePortrait * s;
                }
                Dictionary<GameObject, HeroSelection> boxHero = BoxHero(hsm);
                int snapped = 0;
                if (boxHero != null)
                {
                    foreach (KeyValuePair<GameObject, HeroSelection> kv in boxHero)
                    {
                        if (kv.Key != null && kv.Value != null && kv.Value.sprite != null)
                        {
                            kv.Value.sprite.position = kv.Key.transform.position;
                            kv.Value.sprite.localScale = Vector3.one * kv.Value.selectedPortraitScale;
                            snapped++;
                        }
                    }
                }
                Plugin.Log.LogInfo($"Hero selection layout: box scale {s:0.00}, top y {Plugin.SelectionTopY.Value:0.00}, row gap {Plugin.SelectionRowGap.Value:0.00}, " +
                                   $"box height {boxHeight:0.00}; {snapped} picked portrait(s) re-snapped.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Hero selection layout failed: {e}");
            }
        }

        internal static void LogLayout(HeroSelectionManager hsm, string when)
        {
            try
            {
                Vector4 screen = RewardsUi.ScreenWorld();
                var sb = new StringBuilder();
                sb.Append($"Hero selection {when}: {hsm.boxGO.Length} boxes (HeroCount {Plugin.TargetHeroCount}), second row offset " +
                          $"({Plugin.SelectionRowOffsetX.Value:0.00}, -{Plugin.SelectionRowOffsetY.Value:0.00}). Screen x {screen.x - screen.z:0.00}..{screen.x + screen.z:0.00}, " +
                          $"y {screen.y - screen.w:0.00}..{screen.y + screen.w:0.00}.");
                for (int i = 0; i < hsm.boxGO.Length; i++)
                {
                    GameObject b = hsm.boxGO[i];
                    if (b == null)
                    {
                        continue;
                    }
                    Bounds? v = RewardsUi.VisibleBounds(b.transform);
                    string bounds = v == null ? "no visible sprites" : $"x {v.Value.min.x:0.00}..{v.Value.max.x:0.00}, y {v.Value.min.y:0.00}..{v.Value.max.y:0.00}";
                    sb.Append($"\n  {b.name}: world ({b.transform.position.x:0.00}, {b.transform.position.y:0.00}) {(b.activeSelf ? "" : "[hidden] ")}{bounds}");
                }
                if (hsm.beginAdventureButton != null)
                {
                    sb.Append($"\n  Begin button at ({hsm.beginAdventureButton.position.x:0.00}, {hsm.beginAdventureButton.position.y:0.00})");
                }
                if (hsm.warriorsGO != null)
                {
                    Bounds? roster = RewardsUi.VisibleBounds(hsm.warriorsGO.transform.parent);
                    if (roster != null)
                    {
                        sb.Append($"; hero roster x {roster.Value.min.x:0.00}..{roster.Value.max.x:0.00}, y {roster.Value.min.y:0.00}..{roster.Value.max.y:0.00}");
                    }
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Hero selection layout log failed: {e.Message}");
            }
        }

        // Sandbox "total heroes" 1..3 hides boxes 1..3; hide (and empty) the extra boxes too, otherwise show them.
        [HarmonyPatch(typeof(HeroSelectionManager), nameof(HeroSelectionManager.RefreshCharBoxesBySandboxHeroes))]
        internal static class SandboxPatch
        {
            private static void Postfix(HeroSelectionManager __instance)
            {
                try
                {
                    if (Extra() == 0)
                    {
                        return;
                    }
                    int total = SandboxManager.Instance.IsEnabled() ? SandboxManager.Instance.GetSandboxBoxValue("sbTotalHeroes") : 0;
                    bool limited = total >= 1 && total <= 3;
                    for (int i = HeroSlots.VanillaCount; i < __instance.boxGO.Length; i++)
                    {
                        if (limited)
                        {
                            __instance.ClearBox(i, moveBackHero: false);
                        }
                        if (__instance.boxGO[i].activeSelf == limited)
                        {
                            __instance.boxGO[i].SetActive(!limited);
                        }
                    }
                    if (limited)
                    {
                        Plugin.Log.LogInfo($"Hero selection: sandbox limits the team to {total} heroes, extra boxes hidden (the run gets no extra heroes).");
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Hero selection: sandbox box refresh failed: {e.Message}");
                }
            }
        }

        // PreAssign fills boxes 0..3 from PlayerManager.LastUsedTeam (kept at 4 entries so the profile stays vanilla). Fill the
        // extra boxes from [Selection] LastExtraHeroes, or from [Spike] ExtraHeroes the first time.
        [HarmonyPatch(typeof(HeroSelectionManager), "PreAssign")]
        internal static class PreAssignPatch
        {
            private static void Postfix(HeroSelectionManager __instance)
            {
                try
                {
                    PreAssignExtras(__instance);
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Hero selection: pre-filling the extra boxes failed: {e.Message}");
                }
            }
        }

        private static void PreAssignExtras(HeroSelectionManager hsm)
        {
            int extra = Extra();
            if (extra == 0)
            {
                return;
            }
            string source = Plugin.LastExtraHeroes.Value;
            string from = "[Selection] LastExtraHeroes";
            if (string.IsNullOrWhiteSpace(source))
            {
                source = Plugin.ExtraHeroes.Value;
                from = "[Spike] ExtraHeroes";
            }
            // AssignHeroToBox is async (the box is filled 0.1 s later), so track picks here instead of reading the boxes.
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string id in PlayerManager.Instance.LastUsedTeam ?? new string[0])
            {
                if (!string.IsNullOrEmpty(id))
                {
                    taken.Add(id.Replace(" ", "").ToLowerInvariant());
                }
            }
            var placed = new List<string>();
            int box = HeroSlots.VanillaCount;
            foreach (string raw in (source ?? "").Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (box >= hsm.boxGO.Length)
                {
                    break;
                }
                string id = raw.Trim().ToLowerInvariant();
                if (taken.Contains(id) || !hsm.heroSelectionDictionary.TryGetValue(id, out HeroSelection hs) || hs == null || hs.HeroPicked ||
                    !PlayerManager.Instance.IsHeroUnlocked(id) || hs.DlcBlocked)
                {
                    continue;
                }
                hs.AssignHeroToBox(hsm.boxGO[box]);
                taken.Add(id);
                placed.Add($"{id} -> Box_{box}");
                box++;
            }
            Plugin.Log.LogInfo($"Hero selection: extra boxes pre-filled from {from}: {(placed.Count == 0 ? "none" : string.Join(", ", placed))}.");
        }

        // The dice picks random heroes in a loop until it finds a free one. With more boxes than unlocked heroes there may be none
        // left, and the vanilla guard (available <= 4 && all boxes filled) does not cover a partly filled screen: it would spin forever.
        [HarmonyPatch(typeof(HeroSelectionManager), nameof(HeroSelectionManager.SetRandomHero))]
        internal static class RandomHeroPatch
        {
            private static bool Prefix(HeroSelectionManager __instance)
            {
                try
                {
                    if (Extra() == 0)
                    {
                        return true;
                    }
                    if (!__instance.heroSelectionDictionary.Values.Any(h => h != null && h.RandomAvailable()))
                    {
                        Plugin.Log.LogInfo("Hero selection: no free hero left for the dice.");
                        return false;
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"Hero selection: dice guard failed: {e.Message}");
                }
                return true;
            }
        }

        // Log again once the screen has settled (end of StartCoContinue, the only caller): sprite bounds are known by then.
        [HarmonyPatch(typeof(HeroSelectionManager), "SetCharacterSelectionTabCount")]
        internal static class ScreenReadyLog
        {
            private static void Postfix(HeroSelectionManager __instance)
            {
                if (Extra() > 0)
                {
                    ApplyLayout(__instance);
                    LogLayout(__instance, "ready");
                }
            }
        }
    }
}
