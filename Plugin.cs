using System;
using System.Collections.Generic;
using System.Linq;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;

namespace MoreHeroes
{
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(EnemyScalingGuid, BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.cojoco.moreheroes";
        public const string PluginName = "More Heroes";
        public const string PluginVersion = "0.2.26";
        public const string EnemyScalingGuid = "com.cojoco.enemyscaling";

        internal static ManualLogSource Log;
        private static ConfigFile cfg;

        internal static ConfigEntry<bool> Enabled;
        internal static ConfigEntry<int> HeroCount;
        internal static ConfigEntry<string> ExtraHeroes;
        internal static ConfigEntry<float> HeroSpacing;
        internal static ConfigEntry<float> HeroScale;
        internal static ConfigEntry<float> HeroStagger;
        internal static ConfigEntry<bool> ShowGameErrors;
        internal static ConfigEntry<int> RewardColumns;
        internal static ConfigEntry<float> RewardRowScale;
        internal static ConfigEntry<float> RewardRowSpacing;
        internal static ConfigEntry<float> RewardColumnSpacing;
        internal static ConfigEntry<float> RewardOffsetY;
        internal static ConfigEntry<float> RewardCardScale;
        internal static ConfigEntry<float> RewardCardSpacing;
        internal static ConfigEntry<float> MeterPortraitScale;
        internal static ConfigEntry<float> MeterTextScale;
        internal static ConfigEntry<float> PairsTurnScale;
        internal static ConfigEntry<float> SelectionRowOffsetX;
        internal static ConfigEntry<float> SelectionRowOffsetY;
        internal static ConfigEntry<float> SelectionBoxScale;
        internal static ConfigEntry<float> SelectionTopY;
        internal static ConfigEntry<float> SelectionRowGap;
        internal static ConfigEntry<string> LastExtraHeroes;
        internal static ConfigEntry<bool> ScaleGroupRolls;
        internal static ConfigEntry<float> EventRollScale;
        internal static ConfigEntry<float> EventRollRowSpacing;
        internal static ConfigEntry<float> EventRollOffsetY;
        internal static ConfigEntry<int> EventRollColumns;
        internal static ConfigEntry<float> EventRollColumnSpacing;
        internal static ConfigEntry<float> EventRollCenterX;
        internal static ConfigEntry<float> FinishRunScale;
        internal static ConfigEntry<int> LootRows;
        internal static ConfigEntry<float> LootPanelScale;
        internal static ConfigEntry<float> LootRowSpacing;
        internal static ConfigEntry<float> LootOffsetY;
        internal static ConfigEntry<float> LootCardScale;
        internal static ConfigEntry<float> SidePortraitScale;
        internal static ConfigEntry<float> SidePortraitSpacing;
        internal static ConfigEntry<KeyboardShortcut> SpacingDownKey;
        internal static ConfigEntry<KeyboardShortcut> SpacingUpKey;
        internal static ConfigEntry<KeyboardShortcut> StaggerDownKey;
        internal static ConfigEntry<KeyboardShortcut> StaggerUpKey;

        internal static int TargetHeroCount => Math.Max(HeroSlots.VanillaCount, Math.Min(HeroSlots.MaxCount, HeroCount.Value));

        private void Awake()
        {
            Log = Logger;
            cfg = Config;

            Enabled = Config.Bind("General", "Enabled", true,
                "Add extra heroes to new single-player Adventure runs, and offer HeroCount heroes when you host a co-op room (with MorePlayers). " +
                "Turning this off does not break runs that already have more than 4 heroes. When you join someone else's co-op room, the host's " +
                "settings are used and this is ignored.");
            HeroCount = Config.Bind("General", "HeroCount", 8,
                new ConfigDescription("Total number of heroes in a new single-player Adventure run, and in co-op rooms you host (with MorePlayers; at least the room size). " +
                "When you join someone else's co-op room, the host's count is used and this is ignored.", new AcceptableValueRange<int>(4, 8)));
            ExtraHeroes = Config.Bind("Spike", "ExtraHeroes", "sentinel,assassin,pyromancer,priest",
                "Fallback and first-time default. The extra hero boxes are pre-filled from this list the first time (later from " +
                "[Selection] LastExtraHeroes). If a run begins with fewer than HeroCount heroes picked (e.g. the extra boxes could not be " +
                "added, or not enough heroes are unlocked), the team is topped up from this list, then from any other unlocked hero. " +
                "Comma-separated hero subclass ids. Ids: " + string.Join(", ", MoreHeroes.ExtraHeroes.AllHeroIds));
            LastExtraHeroes = Config.Bind("Selection", "LastExtraHeroes", "",
                "Remembered automatically: the heroes you picked for slots 5..8 last time, used to pre-fill the extra boxes " +
                "(slots 1..4 are remembered by the game itself). Clear it to use [Spike] ExtraHeroes again.");
            HeroSpacing = Config.Bind("Layout", "HeroSpacing", 0f,
                "Distance between heroes in combat when there are more than 4 (vanilla is 1.9). 0 = automatic (fits the screen).");
            HeroScale = Config.Bind("Layout", "HeroScale", 0f,
                "Size of hero models in combat when there are more than 4 (1 = vanilla). 0 = automatic (0.8 with 8 heroes).");
            HeroStagger = Config.Bind("Layout", "HeroStagger", 0.9f,
                "With more than 4 heroes in combat, move the 1st, 3rd, 5th... hero up and the 2nd, 4th, 6th... down by this much, so they overlap less. 0 = no stagger.");
            RewardColumns = Config.Bind("Layout", "RewardColumns", 2,
                new ConfigDescription("Rewards screen with more than 4 heroes: number of columns of hero rows (heroes 1-4 in the first column, 5-8 in the second).",
                    new AcceptableValueRange<int>(1, 4)));
            RewardRowScale = Config.Bind("Layout", "RewardRowScale", 0f,
                "Rewards screen with more than 4 heroes: size of a hero row (1 = vanilla). 0 = automatic (fits the column width and row spacing; about 0.6 with 2 columns at 16:9).");
            RewardRowSpacing = Config.Bind("Layout", "RewardRowSpacing", 0f,
                "Rewards screen with more than 4 heroes: vertical distance between rows of a column, in the rows' own units (vanilla 2.2). 0 = automatic (2.2 for up to 4 rows per column, less for more).");
            RewardColumnSpacing = Config.Bind("Layout", "RewardColumnSpacing", 0f,
                "Rewards screen with more than 4 heroes: horizontal distance between column centres in world units (the screen is about 19.2 wide at 16:9). 0 = automatic (screen width / columns).");
            RewardOffsetY = Config.Bind("Layout", "RewardOffsetY", 0f,
                "Rewards screen with more than 4 heroes: move all rows up (positive) or down (negative), in the rows' own units.");
            RewardCardScale = Config.Bind("Layout", "RewardCardScale", 0f,
                "Rewards screen with more than 4 heroes: size of the reward cards inside a hero row (1 = same as the rest of the row). The shards option moves to just after the last card. 0 = automatic (1.3).");
            RewardCardSpacing = Config.Bind("Layout", "RewardCardSpacing", 0f,
                "Rewards screen with more than 4 heroes: distance between reward card centres, in the cards' own units (vanilla 1.95, a card is about 1.35 wide). 0 = automatic (1.6).");
            MeterPortraitScale = Config.Bind("Layout", "MeterPortraitScale", 0f,
                "Combat stats screen with more than 4 heroes (two heroes between each pair of lines): size of the hero portraits (1 = vanilla). 0 = automatic (0.52).");
            MeterTextScale = Config.Bind("Layout", "MeterTextScale", 0f,
                "Combat stats screen with more than 4 heroes: size of the numbers in the hero rows (1 = vanilla; the totals row is not changed). 0 = automatic (0.85).");
            PairsTurnScale = Config.Bind("Layout", "PairsTurnScale", 0f,
                "Pairs memory minigame with more than 4 heroes: size of the turn-order portraits and their spacing (1 = vanilla). 0 = automatic (fits the row between its vanilla start and the screen edge).");
            SelectionRowOffsetX = Config.Bind("Layout", "SelectionRowOffsetX", 0f,
                "Hero selection with more than 4 heroes: horizontal shift of the second row of boxes (Box_4 sits under Box_0), world units.");
            SelectionBoxScale = Config.Bind("Layout", "SelectionBoxScale", 0.72f,
                "Hero selection with more than 4 boxes: size of the boxes and picked portraits (1 = vanilla).");
            SelectionTopY = Config.Bind("Layout", "SelectionTopY", 3.15f,
                "Hero selection with more than 4 boxes: world y of the top edge of the first row (the header text ends at about 3.6).");
            SelectionRowGap = Config.Bind("Layout", "SelectionRowGap", 0.15f,
                "Hero selection with more than 4 boxes: space between the first row's boxes and the second row's (room for names and ranks).");
            SelectionRowOffsetY = Config.Bind("Layout", "SelectionRowOffsetY", 2.1f,
                "Hero selection with more than 4 heroes: how far below the first row the second row of boxes sits, world units (a box is about 1.9 tall).");
            ScaleGroupRolls = Config.Bind("Balance", "ScaleGroupRolls", true,
                "Events with more than 4 heroes: a group roll adds up one card cost per hero, so 8 heroes roll about twice the total. " +
                "On: the target scales with the number of heroes (x heroes / 4; 8 heroes: '5 or lower' becomes '10 or lower', 'X or higher' " +
                "rounds up and 'X or lower' rounds down for 5-7 heroes). The reply, the odds, the result and critical results all use the scaled target.");
            EventRollColumns = Config.Bind("Layout", "EventRollColumns", 8,
                new ConfigDescription("Events with more than 4 heroes: portraits per row. 8 = one row across the page; 4 = two rows (heroes 5-8 under 1-4).",
                    new AcceptableValueRange<int>(4, 8)));
            EventRollScale = Config.Bind("Layout", "EventRollScale", 0f,
                "Events with more than 4 heroes: size of the hero portraits and roll cards (1 = vanilla). 0 = automatic (about 0.30 for one row across the page; 0.55 for two rows).");
            EventRollColumnSpacing = Config.Bind("Layout", "EventRollColumnSpacing", 0f,
                "Events with more than 4 heroes in one row: distance between the hero columns (world units). 0 = automatic (0.9).");
            EventRollCenterX = Config.Bind("Layout", "EventRollCenterX", 0f,
                "Events with more than 4 heroes in one row: horizontal centre of the row, in the book's local units (vanilla page centre about 3.9). " +
                "0 = automatic (centred on the selected reply, 3.25 before a reply is picked).");
            EventRollRowSpacing = Config.Bind("Layout", "EventRollRowSpacing", 1.6f,
                "Events with more than 4 heroes: vertical distance between the row of heroes 1-4 and the row of heroes 5-8 (world units).");
            EventRollOffsetY = Config.Bind("Layout", "EventRollOffsetY", 0.2f,
                "Events with more than 4 heroes: how far above the vanilla position the first row of hero portraits sits (world units).");
            FinishRunScale = Config.Bind("Layout", "FinishRunScale", 0f,
                "End-of-run screen with more than 4 heroes: size of the hero panels (1 = vanilla). 0 = automatic (0.8 with 8 heroes). " +
                "Rows keep the vanilla spacing for that size, starting where the vanilla first row starts.");
            LootRows = Config.Bind("Layout", "LootRows", 2,
                new ConfigDescription("Loot screen with more than 4 heroes: rows of hero panels. 2 = heroes 5-8 under heroes 1-4 (the item cards are drawn smaller " +
                                      "to make room); 1 = one row across the screen (panels much smaller, cards full size).", new AcceptableValueRange<int>(1, 2)));
            LootPanelScale = Config.Bind("Layout", "LootPanelScale", 0f,
                "Loot screen with more than 4 heroes: size of the hero panels (1 = vanilla). 0 = automatic (0.75 in two rows; about 0.53 for 8 in one row).");
            LootRowSpacing = Config.Bind("Layout", "LootRowSpacing", 0f,
                "Loot screen with 2 rows: vertical distance between the rows (world units). 0 = automatic (2.15 at panel size 0.75).");
            LootOffsetY = Config.Bind("Layout", "LootOffsetY", 0f,
                "Loot screen with more than 4 heroes: move the hero panels up (positive) or down (negative), world units. The item cards follow.");
            LootCardScale = Config.Bind("Layout", "LootCardScale", 0f,
                "Loot screen with more than 4 heroes: size of the item cards (1 = vanilla). 0 = automatic (fills the space below the hero panels: about 0.84 with two rows).");
            SidePortraitScale = Config.Bind("Layout", "SidePortraitScale", 0f,
                "Map, town and events with more than 4 heroes: size of the hero portraits on the left (1 = vanilla). 0 = automatic (0.8, smaller if 8 do not fit the screen height).");
            SidePortraitSpacing = Config.Bind("Layout", "SidePortraitSpacing", 0f,
                "Map, town and events with more than 4 heroes: vertical distance between the portraits on the left (world units; the screen is 10.8 tall). 0 = automatic (vanilla 1.24 times the portrait size).");
            SpacingDownKey = Config.Bind("Hotkeys", "SpacingDown", new KeyboardShortcut(KeyCode.F3),
                "In combat with more than 4 heroes: move heroes closer together (saved to HeroSpacing).");
            SpacingUpKey = Config.Bind("Hotkeys", "SpacingUp", new KeyboardShortcut(KeyCode.F4),
                "In combat with more than 4 heroes: move heroes further apart (saved to HeroSpacing).");
            StaggerDownKey = Config.Bind("Hotkeys", "StaggerDown", new KeyboardShortcut(KeyCode.F5),
                "In combat with more than 4 heroes: less vertical stagger (saved to HeroStagger).");
            StaggerUpKey = Config.Bind("Hotkeys", "StaggerUp", new KeyboardShortcut(KeyCode.F6),
                "In combat with more than 4 heroes: more vertical stagger (saved to HeroStagger).");
            ShowGameErrors = Config.Bind("Logging", "ShowGameErrors", true,
                "The game hides its own errors in release builds. Turn this on to log the game's warnings, errors and exceptions again. They go to Unity's " +
                "Player.log (Linux: ~/.config/unity3d/Dreamsite Games/AcrossTheObelisk/Player.log, Windows: %USERPROFILE%\\AppData\\LocalLow\\Dreamsite Games\\AcrossTheObelisk\\Player.log), " +
                "not to BepInEx/LogOutput.log. Player.log also contains the BepInEx lines.");

            var harmony = new Harmony(PluginGuid);
            var classes = new List<string>();
            var classFailures = new List<string>();
            foreach (Type t in new[]
                     {
                         typeof(MoreHeroes.ExtraHeroes.BeginAdventureScope), typeof(MoreHeroes.ExtraHeroes.SetTeamPatch),
                         typeof(Layout.CalculatePositionXPatch), typeof(Layout.HeroItemInitPatch), typeof(Layout.GenerateHeroesLog),
                         typeof(Layout.CalculateLocalPositionPatch), typeof(Layout.DrawOrderPatch), typeof(Diagnostics.GlobalsAwakePatch),
                         typeof(Guards.SaveSlotPortraitsPatch), typeof(Guards.SidePanelHighlightPatch), typeof(Guards.CorruptionTriesPatch),
                         typeof(RewardsUi.StartPatch), typeof(RewardsUi.CheckAllAssignedPatch), typeof(RewardsUi.ShowRewardsPatch),
                         typeof(RewardsUi.SelectedCardPatch), typeof(DamageMeterUi.ShowPatch), typeof(MinigamesUi.ShowPortraitPatch), typeof(MinigamesUi.PairsStartPatch),
                         typeof(HeroSelectUi.StartPatch), typeof(HeroSelectUi.SandboxPatch), typeof(HeroSelectUi.PreAssignPatch),
                         typeof(HeroSelectUi.RandomHeroPatch), typeof(HeroSelectUi.ScreenReadyLog),
                         typeof(EventProbability.SetProbabilityPatch), typeof(EventProbability.GetProbabilityPatch),
                         typeof(EventsUi.AwakePatch), typeof(EventsUi.SetEventLog), typeof(EventsUi.EventResultLog),
                         typeof(EventsUi.OnResultPatch), typeof(EventsUi.ReplyInitPatch), typeof(EventsUi.IsCriticalPatch),
                         typeof(EventsUi.RollBoxTextPatch), typeof(EventsUi.RecentreRowPatch),
                         typeof(FinishRunUi.StartPatch), typeof(FinishRunUi.PortraitPatch), typeof(FinishRunUi.ProgressionPatch),
                         typeof(FinishRunUi.CalculateGuard), typeof(Diagnostics.DeckWindowLog),
                         typeof(LootUi.StartPatch), typeof(LootUi.SetCharactersPatch), typeof(LootUi.GoldPortraitPatch),
                         typeof(SidePortraits.AwakePatch), typeof(SidePortraits.ShowPatch), typeof(SidePortraits.ResizePatch),
                         typeof(SidePortraits.MapResizePatch), typeof(SidePortraits.CharacterWindowPatch), typeof(NoLeaderboard.Patch)
                     })
            {
                try
                {
                    harmony.CreateClassProcessor(t).Patch();
                    classes.Add(t.Name);
                }
                catch (Exception e)
                {
                    classFailures.Add($"{t.Name} ({e.GetType().Name}: {e.Message})");
                }
            }
            TeamPatches.ApplyAll(harmony);
            IlPatches.ApplyAll(harmony);
            // Last: every method patched so far (by any mod) gets the HarmonyX leave fix, see HarmonyFix.cs.
            HarmonyFix.ApplyToAllPatchedMethods(harmony);

            if (Globals.Instance != null)
            {
                Diagnostics.EnableGameErrorLog();
            }

            // The plugin's own GameObject is destroyed right after startup, so per-frame code (hotkeys, SideClearance) runs on a separate object.
            var runner = new GameObject("MoreHeroes.LayoutTuner") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(runner);
            runner.AddComponent<LayoutTuner>();
            runner.AddComponent<SideClearance>();

            LogSummary(classes, classFailures);
        }

        // BepInEx only reads the .cfg at startup. Screens whose layout is tuned by hand call this when they open, so edits to the
        // file apply without restarting the game. Hotkey changes are saved to the file immediately, so nothing is lost.
        internal static void ReloadConfig()
        {
            try
            {
                cfg?.Reload();
            }
            catch (Exception e)
            {
                Log.LogWarning($"Reloading the config failed, keeping the current values: {e.Message}");
            }
        }

        private void LogSummary(List<string> classes, List<string> classFailures)
        {
            var lines = new List<string>
            {
                $"{PluginName} {PluginVersion} loaded. Enabled={Enabled.Value}, HeroCount={TargetHeroCount}, fallback ExtraHeroes='{ExtraHeroes.Value}', LastExtraHeroes='{LastExtraHeroes.Value}'."
            };
            int ilOk = IlPatches.Specs.Count(s => s.Applied);
            lines.Add($"Transpilers: {ilOk}/{IlPatches.Specs.Count} applied.");
            foreach (MethodSpec s in IlPatches.Specs)
            {
                lines.Add($"  [{(s.Applied ? "ok" : "SKIPPED")}] {s.Name}: {s.Result} ({s.Why})");
            }
            lines.Add($"Prefix/Postfix patches: {TeamPatches.Applied.Count + classes.Count} applied, {TeamPatches.Failed.Count + classFailures.Count} failed.");
            foreach (string s in classes)
            {
                lines.Add("  [ok] " + s);
            }
            foreach (string s in TeamPatches.Applied)
            {
                lines.Add("  [ok] " + s);
            }
            foreach (string s in classFailures.Concat(TeamPatches.Failed))
            {
                lines.Add("  [FAILED] " + s);
            }
            lines.Add($"HarmonyX leave fix: {HarmonyFix.MethodsChecked} patched method(s) checked, {HarmonyFix.Fixed.Count} fixed" +
                      (HarmonyFix.Fixed.Count > 0 ? ": " + string.Join(", ", HarmonyFix.Fixed) : "") +
                      (HarmonyFix.Failed.Count > 0 ? "; FAILED: " + string.Join(", ", HarmonyFix.Failed) : ""));
            bool anyFailed = HarmonyFix.Failed.Count > 0 ||ilOk < IlPatches.Specs.Count || TeamPatches.Failed.Count > 0 || classFailures.Count > 0;
            if (anyFailed)
            {
                Log.LogWarning(string.Join("\n", lines));
            }
            else
            {
                Log.LogInfo(string.Join("\n", lines));
            }
        }
    }
}
