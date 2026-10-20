using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using BattleMatch;
using HarmonyLib;

namespace MoreHeroes
{
    // What the instruction right after a replaced constant must look like. A cheap sanity check on top of the exact
    // occurrence count, so a game update that reshuffles a method is caught instead of patching the wrong constant.
    internal enum Shape
    {
        Branch, // loop bound or comparison: next is a conditional branch or clt/cgt/ceq
        NewArr, // array size: next is newarr
        Sub,    // index arithmetic (i - 4): next is sub
        Elem,   // array index: next is ldelem
        Store,  // initial loop value: next is stloc
        Field,  // value stored in a field: next is stfld
        Rem,    // modulo (x % 4): next is rem
        Any     // e.g. a call argument
    }

    internal class SiteSpec
    {
        public int Constant;
        public int ExpectedCount;
        public (int Ordinal, Shape Shape)[] Replace;
        public MethodInfo ShiftWith; // null = the method's shift (MethodSpec.ShiftWith)
    }

    internal class MethodSpec
    {
        public string Name;
        public Func<MethodBase> Resolve;
        public SiteSpec[] Sites;
        public string Why;
        public MethodInfo ShiftWith; // null = HeroSlots.Shift (combat team size)
        public Func<List<CodeInstruction>, string> Edit; // custom edit instead of constant sites: returns null or the reason it does not match

        // Filled in when the transpiler runs.
        public string Result;
        public bool Applied;
        public int Replaced;
    }

    internal static class IlPatches
    {
        private static readonly MethodInfo ShiftMethod = AccessTools.Method(typeof(HeroSlots), nameof(HeroSlots.Shift));
        private static readonly Dictionary<MethodBase, MethodSpec> ByMethod = new Dictionary<MethodBase, MethodSpec>();

        internal static readonly List<MethodSpec> Specs = new List<MethodSpec>();

        private static SiteSpec S(int constant, int expected, params (int, Shape)[] replace)
        {
            return new SiteSpec { Constant = constant, ExpectedCount = expected, Replace = replace };
        }

        // Same as S, but these occurrences follow a different count than the rest of the method (e.g. team loops in a method
        // whose other constants follow the number of minigame turn slots).
        private static SiteSpec S(MethodInfo shiftWith, int constant, int expected, params (int, Shape)[] replace)
        {
            return new SiteSpec { Constant = constant, ExpectedCount = expected, Replace = replace, ShiftWith = shiftWith };
        }

        private static MethodSpec M(string name, Func<MethodBase> resolve, string why, params SiteSpec[] sites)
        {
            return new MethodSpec { Name = name, Resolve = resolve, Why = why, Sites = sites };
        }

        // Same as M, but the constants follow a different count (e.g. the number of reward rows or hero selection boxes).
        private static MethodSpec M(string name, Func<MethodBase> resolve, MethodInfo shiftWith, string why, params SiteSpec[] sites)
        {
            return new MethodSpec { Name = name, Resolve = resolve, Why = why, Sites = sites, ShiftWith = shiftWith };
        }

        // A custom IL edit (not a constant shift). The edit verifies the IL itself and returns an error if it does not match.
        private static MethodSpec E(string name, Func<MethodBase> resolve, string why, Func<List<CodeInstruction>, string> edit)
        {
            return new MethodSpec { Name = name, Resolve = resolve, Why = why, Sites = new SiteSpec[0], Edit = edit };
        }

        private static MethodBase Mm(string name)
        {
            return AccessTools.Method(typeof(MatchManager), name);
        }

        private static MethodBase Co(Type type, string name)
        {
            MethodInfo m = AccessTools.Method(type, name);
            return m == null ? null : AccessTools.EnumeratorMoveNext(m);
        }

        // Expected counts come from the IL of the game build this was written against (see NOTES.md for the survey).
        // Ordinals count occurrences of that constant in the method, in IL order, starting at 0.
        private static void Define()
        {
            Specs.Clear();
            Specs.Add(M("BattleTeamManager.GenerateHeroes", () => AccessTools.Method(typeof(BattleTeamManager), "GenerateHeroes"),
                "new Hero[4] holding the spawned heroes",
                S(4, 2, (0, Shape.NewArr))));
            Specs.Add(M("BattleTeamManager.ReloadCombatFullAction", () => AccessTools.Method(typeof(BattleTeamManager), "ReloadCombatFullAction"),
                "hero loop restoring HP/items on combat reload",
                S(4, 2, (1, Shape.Branch))));
            Specs.Add(M("MatchManager.InitializeVars", () => Mm("InitializeVars"),
                "pendingHeroHandAdds = new int[4]",
                S(4, 1, (0, Shape.NewArr))));
            Specs.Add(M("MatchManager.GenerateDecks", () => Mm("GenerateDecks"),
                "per-hero deck array new List<string>[4]",
                S(4, 1, (0, Shape.NewArr))));
            Specs.Add(M("MatchManager.GetHeroItemsForTurnSave", () => Mm("GetHeroItemsForTurnSave"),
                "turn save: hero items loop",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("MatchManager.GetHeroFromId", () => Mm("GetHeroFromId"),
                "hero lookup loop",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("MatchManager.PositionIsMiddle", () => Mm("PositionIsMiddle"),
                "hero loop for 'middle position' targeting",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("MatchManager.CardNamesForSyncCode", () => Mm("CardNamesForSyncCode"),
                "hero loop in the sync code (desync check hash)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("MatchManager.GetHero(int)", () => AccessTools.Method(typeof(MatchManager), "GetHero", new[] { typeof(int) }),
                "bounds check _order < 4",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("MatchManager.SetCharactersPing", () => Mm("SetCharactersPing"),
                "emote bounds check heroActive < 4",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("MatchManager.GetTargetByNum", () => Mm("GetTargetByNum"),
                "keyboard target order key \"H\" + (3 - Position)",
                S(3, 1, (0, Shape.Any))));
            Specs.Add(M("MatchManager.CreateLogResult", () => Mm("CreateLogResult"),
                "combat log: combined loop i < 8, i < 4 ? hero : npc[i - 4]",
                S(4, 4, (0, Shape.Branch), (1, Shape.Sub), (2, Shape.Branch), (3, Shape.Branch)),
                S(8, 1, (0, Shape.Branch))));
            Specs.Add(M("MatchManager.BeginMatch", () => Co(typeof(MatchManager), "BeginMatch"),
                "corruption setup: 3 hero loops (2 NPC loops left alone)",
                S(4, 7, (2, Shape.Branch), (3, Shape.Branch), (5, Shape.Branch))));
            Specs.Add(M("MatchManager.NextTurnContinue", () => Co(typeof(MatchManager), "NextTurnContinue"),
                "corruption each round: 2 hero loops and 2 random-hero picks (a random pick limited to 0..3 would spin forever once heroes 1-4 are dead)",
                S(4, 18, (6, Shape.Branch), (7, Shape.Any), (12, Shape.Branch), (13, Shape.Any))));
            Specs.Add(M("MatchManager.CastCard", () => Co(typeof(MatchManager), "CastCard"),
                "2 hero loops (add card to target hero, extreme block index)",
                S(4, 12, (7, Shape.Branch), (9, Shape.Branch))));
            Specs.Add(M("MatchManager.FixTOTALCo", () => Co(typeof(MatchManager), "FixTOTALCo"),
                "restore combat from sync code (resume from save / reload): heroes+NPC combined loops and the array indices after the hero block",
                S(4, 10, (0, Shape.Branch), (1, Shape.Sub), (2, Shape.Branch), (3, Shape.Branch), (4, Shape.Sub), (6, Shape.Branch), (9, Shape.Sub)),
                S(8, 6, (0, Shape.Elem), (2, Shape.Branch), (5, Shape.Branch)),
                S(9, 3, (1, Shape.Elem)),
                S(11, 2, (0, Shape.Elem)),
                S(12, 3, (1, Shape.Branch), (2, Shape.Elem))));
            Specs.Add(M("BattleKeyboard.ShowCombatKeyboard", () => AccessTools.Method(typeof(BattleKeyboard), "ShowCombatKeyboard"),
                "keyboard numbers: hero loop from 3 down, 2 hero hide loops",
                S(3, 1, (0, Shape.Store)),
                S(4, 5, (1, Shape.Branch), (3, Shape.Branch))));
            Specs.Add(M("CharacterItem.KillCharacterCO", () => Co(typeof(CharacterItem), "KillCharacterCO"),
                "on-kill events: 2 hero loops",
                S(4, 3, (0, Shape.Branch), (1, Shape.Branch))));
            Specs.Add(M("MapManager.DoCorruptionReward", () => Co(typeof(MapManager), "DoCorruptionReward"),
                "map corruption rewards: 4 gold/dust hero loops and the random card upgrade loop (occurrence 2 is a coroutine state)",
                S(4, 6, (0, Shape.Branch), (1, Shape.Branch), (3, Shape.Branch), (4, Shape.Branch), (5, Shape.Branch))));

            // Rewards screen (0.2.0): loops over the reward rows. They follow the number of rows that exist (RewardsUi), so if
            // cloning rows failed the loops stay at 4 instead of running past the end of characterRewardArray.
            MethodInfo rows = AccessTools.Method(typeof(RewardsUi), nameof(RewardsUi.Shift));
            Specs.Add(M("RewardsManager.ShowRewardsCo", () => Co(typeof(RewardsManager), "ShowRewardsCo"), rows,
                "rewards: show a row per hero (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("RewardsManager.SetCardReward", () => AccessTools.Method(typeof(RewardsManager), "SetCardReward"), rows,
                "rewards: pass a card click to every row (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("RewardsManager.ControllerMovement", () => AccessTools.Method(typeof(RewardsManager), "ControllerMovement"), rows,
                "rewards: controller navigation over the rows (i < 4)",
                S(4, 1, (0, Shape.Branch))));

            // Hero selection (0.2.0): loops over the hero boxes. They follow the number of boxes (HeroSelectUi); boxes are only
            // cloned in single-player Adventure, so every other mode keeps 4.
            MethodInfo boxes = AccessTools.Method(typeof(HeroSelectUi), nameof(HeroSelectUi.Shift));
            Specs.Add(M("HeroSelectionManager.AllBoxWithHeroes", () => AccessTools.Method(typeof(HeroSelectionManager), "AllBoxWithHeroes"), boxes,
                "Begin readiness: filled boxes == 4",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("HeroSelectionManager.IsHeroSelected", () => AccessTools.Method(typeof(HeroSelectionManager), "IsHeroSelected"), boxes,
                "is this hero already in a box (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("HeroSelectionManager.SetRandomHero", () => AccessTools.Method(typeof(HeroSelectionManager), "SetRandomHero"), boxes,
                "dice: count filled boxes (i < 4) and 'all boxes full' check (available <= 4 && filled == 4)",
                S(4, 3, (0, Shape.Branch), (1, Shape.Branch), (2, Shape.Branch))));
            Specs.Add(M("HeroSelectionManager.BeginAdventure", () => AccessTools.Method(typeof(HeroSelectionManager), "BeginAdventure"), boxes,
                "picked subclass array new string[4] (LastUsedTeam's new string[4] and j < 4 stay vanilla)",
                S(4, 3, (0, Shape.NewArr))));
            Specs.Add(M("HeroSelectionManager.HideCharacterArrowController", () => AccessTools.Method(typeof(HeroSelectionManager), "HideCharacterArrowController"), boxes,
                "controller arrows over the boxes (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("HeroSelectionManager.ControllerMovement", () => AccessTools.Method(typeof(HeroSelectionManager), "ControllerMovement"), boxes,
                "controller navigation: box loop j < 4 (#1); the inner k < 4 (#0) is the 4 player name slots of a box",
                S(4, 2, (1, Shape.Branch))));
            Specs.Add(M("PerkTree.DoTeamPerks", () => AccessTools.Method(typeof(PerkTree), "DoTeamPerks"), boxes,
                "perk tree in hero selection: teammates' perks, single-player box loop (#0; #1 is the multiplayer loop)",
                S(4, 2, (0, Shape.Branch))));

            // Events (0.2.3). Roll loops follow the roll portraits that exist (EventsUi.RollExtra), the outcome loop follows the
            // heroes that have a CharWinner entry (EventsUi.HeroExtra); both stay 4 if growing the event arrays failed.
            MethodInfo rollSlots = AccessTools.Method(typeof(EventsUi), nameof(EventsUi.ShiftRollSlots));
            MethodInfo eventHeroes = AccessTools.Method(typeof(EventsUi), nameof(EventsUi.ShiftEventHeroes));
            Specs.Add(M("EventManager.ShowCharactersRoll", () => Co(typeof(EventManager), "ShowCharactersRoll"), rollSlots,
                "event roll: a portrait and roll data per hero (i < 4, #0; #1 is a string array index)",
                S(4, 2, (0, Shape.Branch))));
            Specs.Add(M("EventManager.RollCards", () => Co(typeof(EventManager), "RollCards"), rollSlots,
                "event roll: deal a card per hero (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("EventManager.FinalResolution", () => AccessTools.Method(typeof(EventManager), "FinalResolution"), eventHeroes,
                "event outcome per hero: XP, cards, perks, card removal, HP, gold/dust, items (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("EventManager.Finish", () => AccessTools.Method(typeof(EventManager), "Finish"),
                "character replacement position < 4 (SwapCharacter itself checks team.Size)",
                S(4, 1, (0, Shape.Branch))));
            // End of run (0.2.5): "heroes owned by this player == 4" picks the single-player gold/dust payout (#0). The other 4s
            // are an act number string and Traits[4] for the run history, which keeps 4 heroes.
            Specs.Add(M("FinishRunManager.CalculateFinishRunReward", () => AccessTools.Method(typeof(FinishRunManager), "CalculateFinishRunReward"),
                AccessTools.Method(typeof(FinishRunUi), nameof(FinishRunUi.Shift)),
                "end of run gold/dust: single-player payout when the player owns all 4 heroes (owned == 4)",
                S(4, 6, (0, Shape.Branch))));
            // Loot (0.2.6): loops over the hero panels follow the panels that exist (LootUi). activeCharacter = 4 means "no hero
            // picking" and the pick handlers return when activeCharacter >= 4; both move together. The other 4s in these classes
            // are coroutine states, GetChild(4) (pet slot) and the multiplayer order/RPC code.
            MethodInfo lootPanels = AccessTools.Method(typeof(LootUi), nameof(LootUi.Shift));
            Specs.Add(M("LootManager.Start", () => AccessTools.Method(typeof(LootManager), "Start"), lootPanels,
                "loot: hide every panel and clear looted[] (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("LootManager.SetCharacters", () => AccessTools.Method(typeof(LootManager), "SetCharacters"), lootPanels,
                "loot: assign a hero to each panel (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("LootManager.ShowCharacters", () => Co(typeof(LootManager), "ShowCharacters"), lootPanels,
                "loot: show each panel (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("LootManager.ActivateCharacterCo", () => Co(typeof(LootManager), "ActivateCharacterCo"), lootPanels,
                "loot: highlight the hero whose turn it is (i < 4, #1; #0 is a coroutine state)",
                S(4, 2, (1, Shape.Branch))));
            Specs.Add(M("LootManager.NextCharacter", () => AccessTools.Method(typeof(LootManager), "NextCharacter"), lootPanels,
                "loot: next hero who has not picked (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("LootManager.Looted", () => AccessTools.Method(typeof(LootManager), "Looted"), lootPanels,
                "loot: item picked; activeCharacter >= 4 guard and activeCharacter = 4 (nobody picking)",
                S(4, 2, (0, Shape.Branch), (1, Shape.Field))));
            Specs.Add(M("LootManager.LootGold", () => AccessTools.Method(typeof(LootManager), "LootGold"), lootPanels,
                "loot: gold picked; activeCharacter >= 4 guard and activeCharacter = 4 (nobody picking)",
                S(4, 2, (0, Shape.Branch), (1, Shape.Field))));
            Specs.Add(M("LootManager.HighLight", () => AccessTools.Method(typeof(LootManager), "HighLight"), lootPanels,
                "loot: highlight the equipment slot of the hero picking (activeCharacter < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("LootManager.ControllerMovement", () => AccessTools.Method(typeof(LootManager), "ControllerMovement"), lootPanels,
                "loot: controller navigation over the panels (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            // Pairs memory game (0.2.22): turn slots follow MinigamesUi.PairsShift (team size rounded up to even); the two loops
            // over theTeam follow the team size.
            MethodInfo pairs = AccessTools.Method(typeof(MinigamesUi), nameof(MinigamesUi.PairsShift));
            Func<string, MethodBase> pm = n => AccessTools.Method(typeof(CardPlayerPairsManager), n);
            Specs.Add(M("CardPlayerPairsManager.Start", () => pm("Start"), pairs,
                "pairs game: turn order from the team (team loop i < 4, pad to 4 slots, new int[4], copy loop)",
                S(4, 5, (1, Shape.Branch), (2, Shape.Branch), (3, Shape.NewArr), (4, Shape.Branch)),
                S(ShiftMethod, 4, 5, (0, Shape.Branch))));
            Specs.Add(M("CardPlayerPairsManager.NextRound", () => pm("NextRound"), pairs,
                "pairs game: reshuffle the turn order every 4 flips (currentRound % 4)",
                S(4, 1, (0, Shape.Rem))));
            Specs.Add(M("CardPlayerPairsManager.SetCharacterOrder", () => pm("SetCharacterOrder"), pairs,
                "pairs game: turn portraits loop (i < 4)",
                S(4, 1, (0, Shape.Branch))));
            Specs.Add(M("CardPlayerPairsManager.ShowCurrentCharacter", () => pm("ShowCurrentCharacter"), pairs,
                "pairs game: highlight the flipping hero (i < 4, currentRound % 4)",
                S(4, 2, (0, Shape.Rem), (1, Shape.Branch))));
            Specs.Add(M("CardPlayerPairsManager.ShowPlayerName", () => pm("ShowPlayerName"), pairs,
                "pairs game: name of the flipping hero (currentRound % 4); occurrence 1 is a string.Format argument array",
                S(4, 2, (0, Shape.Rem))));
            Specs.Add(M("CardPlayerPairsManager.CanClick", () => pm("CanClick"), pairs,
                "pairs game: whose flip it is (currentRound % 4)",
                S(4, 1, (0, Shape.Rem))));
            Specs.Add(M("CardPlayerPairsManager.SelectCardCo", () => Co(typeof(CardPlayerPairsManager), "SelectCardCo"), pairs,
                "pairs game: give a found pair to the two flipping heroes (charSpr[currentRound % 4 - 1], [% 4], team loop); occurrence 3 is a coroutine state",
                S(4, 4, (0, Shape.Rem), (1, Shape.Rem)),
                S(ShiftMethod, 4, 4, (2, Shape.Branch))));
            Specs.Add(M("CharacterMeter.DoStats", () => AccessTools.Method(typeof(CharacterMeter), "DoStats"),
                AccessTools.Method(typeof(DamageMeterUi), nameof(DamageMeterUi.Shift)),
                "combat stats: totals and best-hero loop over the rows shown (j < 4); occurrences 0, 2, 3 are column checks i > 4",
                S(4, 4, (1, Shape.Branch))));
            Specs.Add(E("CharacterReward.DoCard", () => AccessTools.Method(typeof(CharacterReward), "DoCard"),
                "rewards: card spacing x = -1.9 + position * 1.95 ([Layout] RewardCardSpacing, more than 4 heroes only)",
                RewardsUi.EditCardPitch));
            Specs.Add(E("EventManager.EventResult", () => Co(typeof(EventManager), "EventResult"),
                "group roll: card total compared with the target scaled by heroes/4 ([Balance] ScaleGroupRolls)",
                EventsUi.EditGroupCompare));
        }

        internal static bool FixTotalApplied
        {
            get
            {
                MethodSpec s = Specs.FirstOrDefault(x => x.Name == "MatchManager.FixTOTALCo");
                return s != null && s.Applied;
            }
        }

        internal static void ApplyAll(Harmony harmony)
        {
            Define();
            var transpiler = new HarmonyMethod(AccessTools.Method(typeof(IlPatches), nameof(Transpiler)));
            foreach (MethodSpec spec in Specs)
            {
                try
                {
                    MethodBase target = spec.Resolve();
                    if (target == null)
                    {
                        spec.Result = "method not found";
                        continue;
                    }
                    ByMethod[target] = spec;
                    harmony.Patch(target, transpiler: transpiler);
                    if (spec.Result == null)
                    {
                        spec.Result = "transpiler did not run";
                    }
                }
                catch (Exception e)
                {
                    spec.Applied = false;
                    spec.Result = "patch threw " + e.GetType().Name + ": " + e.Message;
                }
            }
        }

        private static int? IntConstant(CodeInstruction ci)
        {
            OpCode op = ci.opcode;
            if (op == OpCodes.Ldc_I4_M1) return -1;
            if (op == OpCodes.Ldc_I4_0) return 0;
            if (op == OpCodes.Ldc_I4_1) return 1;
            if (op == OpCodes.Ldc_I4_2) return 2;
            if (op == OpCodes.Ldc_I4_3) return 3;
            if (op == OpCodes.Ldc_I4_4) return 4;
            if (op == OpCodes.Ldc_I4_5) return 5;
            if (op == OpCodes.Ldc_I4_6) return 6;
            if (op == OpCodes.Ldc_I4_7) return 7;
            if (op == OpCodes.Ldc_I4_8) return 8;
            if ((op == OpCodes.Ldc_I4_S || op == OpCodes.Ldc_I4) && ci.operand != null)
            {
                try
                {
                    return Convert.ToInt32(ci.operand);
                }
                catch (Exception)
                {
                    return null;
                }
            }
            return null;
        }

        private static bool ShapeMatches(Shape shape, CodeInstruction next)
        {
            if (next == null)
            {
                return false;
            }
            OpCode op = next.opcode;
            switch (shape)
            {
                case Shape.Branch:
                    return op.FlowControl == FlowControl.Cond_Branch || op == OpCodes.Clt || op == OpCodes.Clt_Un || op == OpCodes.Cgt
                           || op == OpCodes.Cgt_Un || op == OpCodes.Ceq;
                case Shape.NewArr:
                    return op == OpCodes.Newarr;
                case Shape.Sub:
                    return op == OpCodes.Sub;
                case Shape.Elem:
                    return op == OpCodes.Ldelem_Ref || op == OpCodes.Ldelem_I4 || op == OpCodes.Ldelem;
                case Shape.Store:
                    return op == OpCodes.Stloc || op == OpCodes.Stloc_S || op == OpCodes.Stloc_0 || op == OpCodes.Stloc_1
                           || op == OpCodes.Stloc_2 || op == OpCodes.Stloc_3;
                case Shape.Field:
                    return op == OpCodes.Stfld;
                case Shape.Rem:
                    return op == OpCodes.Rem;
                default:
                    return true;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, MethodBase original)
        {
            List<CodeInstruction> list = instructions.ToList();
            if (original == null || !ByMethod.TryGetValue(original, out MethodSpec spec))
            {
                Plugin.Log.LogError($"Transpiler called for an unknown method {original?.DeclaringType?.Name}.{original?.Name}; leaving it unchanged.");
                return list;
            }

            if (spec.Edit != null)
            {
                var copy = new List<CodeInstruction>(list);
                string error;
                try
                {
                    error = spec.Edit(copy);
                }
                catch (Exception e)
                {
                    error = "edit threw " + e.GetType().Name + ": " + e.Message;
                }
                if (error != null)
                {
                    return Fail(spec, list, error);
                }
                spec.Applied = true;
                spec.Replaced = 1;
                spec.Result = "ok, custom edit applied";
                return copy;
            }

            // Pass 1: find every site and verify counts and shapes. Nothing is changed unless everything matches.
            var toShift = new List<int>();
            var shiftAt = new Dictionary<int, MethodInfo>();
            foreach (SiteSpec site in spec.Sites)
            {
                var positions = new List<int>();
                for (int i = 0; i < list.Count; i++)
                {
                    if (IntConstant(list[i]) == site.Constant)
                    {
                        positions.Add(i);
                    }
                }
                if (positions.Count != site.ExpectedCount)
                {
                    return Fail(spec, list, $"expected {site.ExpectedCount} x constant {site.Constant}, found {positions.Count}");
                }
                foreach ((int ordinal, Shape shape) in site.Replace)
                {
                    int idx = positions[ordinal];
                    CodeInstruction next = idx + 1 < list.Count ? list[idx + 1] : null;
                    if (!ShapeMatches(shape, next))
                    {
                        return Fail(spec, list, $"constant {site.Constant} #{ordinal}: expected {shape} after it, found {next?.opcode.Name ?? "end"}");
                    }
                    toShift.Add(idx);
                    shiftAt[idx] = site.ShiftWith ?? spec.ShiftWith ?? ShiftMethod;
                }
            }

            // Pass 2: insert "call HeroSlots.Shift" (or the site's / method's own shift) after each chosen constant. Inserting after (not replacing) keeps labels and
            // exception blocks on the original instruction. Insert from the end so earlier indices stay valid.
            foreach (int idx in toShift.Distinct().OrderByDescending(x => x))
            {
                list.Insert(idx + 1, new CodeInstruction(OpCodes.Call, shiftAt[idx]));
            }
            spec.Applied = true;
            spec.Replaced = toShift.Count;
            spec.Result = $"ok, {toShift.Count} constant(s) now follow the hero count";
            return list;
        }

        private static List<CodeInstruction> Fail(MethodSpec spec, List<CodeInstruction> list, string reason)
        {
            spec.Applied = false;
            spec.Replaced = 0;
            spec.Result = "SKIPPED, IL does not match (game updated?): " + reason;
            Plugin.Log.LogError($"[IL] {spec.Name}: {spec.Result}. The method is left as vanilla.");
            return list;
        }
    }
}
