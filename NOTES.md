# MoreHeroes: developer notes (phase 1 spike)

Goal of phase 1: a single-player Adventure run with up to 8 heroes in combat, playable to the end of the fight, failing safely.
Multiplayer and proper UI are later phases. Written blind (not yet run in game).

Line numbers refer to the ilspycmd decompile of the game build current on 2026-10-19 (Unity 2022.3.62f2), namespace
subfolders as ilspycmd writes them (e.g. `BattleMatch/Team.cs`). No game source is copied here.

## How it works

- **`HeroSlots.Count`** is the hero slot count: `AtOManager.team.Size` clamped to 4..8 in single player, always 4 in multiplayer.
  Everything keys off the team size, not the config, so a vanilla-size team behaves exactly like vanilla, an 8-hero save keeps
  working with `Enabled=false`, and if adding heroes fails the game just has 4.
- **Transpilers** (`IlPatches.cs`) never replace a constant. They keep the `ldc.i4 K` and insert `call HeroSlots.Shift(int)`
  after it, which returns `K + Count - 4`. So 4 becomes Count, 3 becomes Count-1, 8 (heroes+NPCs) becomes Count+4, and
  sync-code indices after the hero block (8, 9, 11, 12) move by the number of extra heroes. Inserting after the constant keeps
  branch labels and exception blocks intact.
- Each transpiled method has a spec: for each constant, the exact number of occurrences in the method and which ones (by
  ordinal, IL order) to shift, plus a check on the next instruction (branch / newarr / sub / ldelem / stloc). If anything differs,
  the method is left vanilla and an error is logged. Counts were taken from the IL (`ilspycmd -il`) and then verified by running
  the transpiler against `PatchProcessor.GetOriginalInstructions` for every target in an offline harness: all 18 apply.
- **Extra heroes** (`ExtraHeroes.cs`): Postfix on `AtOManager.SetTeamFromSubClasses` (AtOManager.cs:1583), only while
  `HeroSelectionManager.BeginAdventure` (HeroSelectionManager.cs:2524) is running. That call creates the 4 picked heroes with the
  private `AtOManager.CreateHeroBySubClass` (AtOManager.cs:1596: perk rank, starting item, skin, cardback, perk list). The
  postfix calls the same method for each extra id and appends the hero (`team.heroes.Add` + `team.SetHero`, which also sets
  `HeroIndex`). Right after, `AtOManager.BeginAdventure` (AtOManager.cs:5939) treats every team member the same: subclass
  traits, `ReassignInitialItem`, level-0 traits, and `InitGame` sets `Owner` to the local player for all heroes.
  Before creating an extra, the mod fills the entries `CreateHeroBySubClass` reads that only picked heroes have:
  `PerksManager.heroPerks[id]` (from `PlayerManager.GetHeroPerks(id, true)`, as `PerksManager.SetPlayerPerks` does for picks),
  `HeroSelectionManager.playerHeroSkinsDict[id]` and `playerHeroCardbackDict[id]`. If that call throws, a plain
  `GameManager.CreateHero` + `ReassignInitialItem` + perk list is used.
  Skipped for: multiplayer (warning), non-Adventure modes, tutorial/first game, combat tool, empty team slots (sandbox with fewer
  heroes), `HeroCount=4`, `Enabled=false`. `TravelBetweenZones` (AtOManager.cs:1138) also calls `SetTeamFromSubClasses` for zones that
  swap the team; the scope flag keeps the postfix out of that.
  Hero choice: `[Spike] ExtraHeroes` in order (must exist, DLC owned, not already in the team), then every other hero the game
  knows (GameManager.cs:1313 list) that is unlocked and owned.

## Survey of hardcoded 4s on the combat path

Classes: **hero** = hero team size, **npc** = NPC team size (stays 4), **equip** = equipment/enchant slots, **mp** = multiplayer
only, **other** = unrelated number (enum value, array field index, state machine state, rarity, round number...).

### Patched

| Where (decompile) | What the 4 is | How |
|---|---|---|
| BattleMatch/Team.cs:46, 63 (`Team` ctor, `Clear`) | `new Character[4]` slots, used for both teams | Postfix: hero team padded to Count (NPC team untouched) |
| BattleMatch/BattleTeamManager.cs:368 `GenerateHeroes` | `new Hero[4]` spawned heroes | transpiler (4 #0). 4 #1 at :396 is `list[4]` = pet item slot, equip |
| BattleTeamManager.cs:493 `ReloadCombatFullAction` | hero loop | transpiler (4 #1); 4 #0 at :506 is pet slot, equip |
| BattleMatch/TurnSaveManager.cs:16 `heroLifeArr = new int[4]` | per-hero HP for turn save | Prefix on `UpdateHeroLife` grows it; `GetHeroLifeByIndex` returns current HP instead of throwing when an old save has fewer entries |
| MatchManager.cs:1579 `InitializeVars` | `pendingHeroHandAdds = new int[4]` | transpiler |
| MatchManager.cs:1874 `GenerateDecks` | per-hero deck `new List<string>[4]` | transpiler |
| MatchManager.cs:2014 `GetHeroItemsForTurnSave` | hero loop (turn save) | transpiler |
| MatchManager.cs:2579, 2595, 2628 `BeginMatch` | corruption hero loops | transpiler (4 #2, #3, #5 of 7). :2605, :2652 are NPC loops; others are an array index and a state number |
| MatchManager.cs:3902, 3916, 4122, 4136 `NextTurnContinue` | corruption: hero loops and `GetRandomIntRange(0, 4)` random hero | transpiler (4 #6, #7, #12, #13 of 18). The random pick is in a `while` that retries until it hits a live hero, so with 0..3 it would spin forever once heroes 1-4 are dead. NPC loops/randoms (:3809, :3830, :3934, :3948, :4154, :4168) and `itemTarget == 4` enum checks untouched |
| MatchManager.cs:5390 `GetTargetByNum` | keyboard target key `"H" + (3 - Position)` | transpiler (3 -> Count-1), keeps keys "H0".."H7" sorted |
| MatchManager.cs:5807 `GetHeroFromId` | hero loop | transpiler |
| MatchManager.cs:7563, 7720 `CastCard` | hero loops (add card to target hero, extreme block index) | transpiler (4 #7, #9 of 12). Others: string array index, state, `DiscardCard(..., 4)` and `CardFrom == 4` enum values |
| MatchManager.cs:10095 `PositionIsMiddle` | hero loop | transpiler |
| MatchManager.cs:10839 `CardNamesForSyncCode` | hero loop in sync-code hash | transpiler (only matters for MP desync checks; patched so the hash covers all heroes) |
| MatchManager.cs:13403 `GetHero(int)` | `_order < 4` bound | transpiler. `GetNPC` (:13412) is npc |
| MatchManager.cs:15704-15922 `FixTOTALCo` | resume-from-sync-code (mid-combat save, `ReloadCombat`): `i < 8` heroes+NPCs, `k < 4 ? hero : npc[k - 4]`, and `codeArr[8]` round, `[9]` enchantments, `[11]` random index, `[12]` scarab, `Length > 12` | transpiler: 14 constants. `GenerateSyncCode` (:16053) already writes `TeamHero.Count` hero blocks, so the decoder must shift. Per-character field indices (`aux[4]`, `aux[8]`...) untouched |
| MatchManager.cs:16373 `SetCharactersPing` | `heroActive < 4` | transpiler |
| MatchManager.cs:16597-16682 `CreateLogResult` | combat log combined loop `i < 8`, `i < 4`, `i - 4` | transpiler (all 4s and the 8) |
| BattleMatch/BattleKeyboard.cs:81, 152, 178 `ShowCombatKeyboard` | keyboard numbers: hero loop from 3 down, 2 hero hide loops | transpiler (3 #0; 4 #1, #3 of 5). :100, :159, :185 are NPC |
| CharacterItem.cs:2945, 3000 `KillCharacterCO` | on-kill event hero loops | transpiler |
| AtOManager.cs:396 `InitCombatStats` `new int[4, 12]` | per-hero combat stats, indexed by `HeroIndex` (Trait.cs:1554 and others) | Prefix on `InitCombatStatsCurrent` (runs at combat start and on turn load) grows the rows, keeping data |
| AtOManager.cs:473, 1439 `craftedCards` (4 dictionaries) | per-hero crafted card counts | Prefix on `AddCraftedCard` pads the list |
| CharacterItem.cs:2282 `CalculatePositionX` | hero x = -2.4 - pos*1.9 | Postfix when Count > 4 (see Layout) |
| SideCharacters.cs:144 `CharacterIconPosition` | map portraits array of 4 | Prefix clamps the index (CardPlayerManager.cs:514 loops `team.Size`) |

### Not patched (on purpose)

| Where | Class | Why |
|---|---|---|
| GameTeam.cs:9 `EMPTY_TEAM = new Hero[4]` | hero | Only the initial list of a new team. `heroes` is a `List`; `SetHeroes` takes any length; extras are appended. No patch needed |
| NetworkManager.cs:472, 1561 `PlayerHeroPositionOwner = new string[4]` | mp | Read by `InitGameMP` (AtOManager.cs:1326), `PerksManager.SetPlayerPerks` MP branch (PerksManager.cs:115), AtOManager.cs:3576, CurrencyManager.cs:277. SP uses `InitGame` (owner = local nick). Phase 3 |
| MatchManager.cs:2105-2150 `NET_ShareDecks` | mp | `NET_SaveCardDeck(type, a, b, c, d)` has 4 fixed slots. Phase 3 |
| MatchManager.cs:15423-15540 `FixCodeSyncFromMaster` | mp | client-side desync repair, same combined layout as FixTOTALCo. Phase 3 |
| MatchManager.cs:1485 `GO_DeckPileCards = new GameObject[4]`, DrawDeckPile :12474, :12490 | other | draw pile thickness visuals, not heroes |
| MatchManager.cs:1916 `GenerateDecksNPCs`; BattleTeamManager.cs:88 `InitNPCTeam`, :261, :274 `CreateNPC`; InitiativesManager.cs:110 | npc | NPC team stays 4 (InitiativesManager hero side already uses `Count`) |
| AtOManager.cs:1570, 3188 `teamNPCAtO`, :3237, :3250 | npc | |
| MatchManager.cs:1302, 1342 `turnDataForMP[4]`, :2966 `array[4]`, :4651 `validTimes > 3`, :8715, :14575, :14584 | other | array field indices, retry counters, NG+/madness levels |
| Character.cs ~25 sites (`i == 4` / `i != 4` in item loops, :8188-8345 `< 3`, `< 4`) | equip | pet slot / enchant slots / equipment slots |
| Character.cs:3186, 3827; Scarab.cs:159; BattleMatch.Actions/IncreaseReduceCurseAurasAction.cs:50 | other | round 4 perk, spellsword charges, scarab types, 4 aura/curse modes |
| AtOManager.cs:349, 754, 861, 952, 3668 | mp | debug/MP share and NET_SetTeam |
| CharacterMeter.cs:77 | hero (UI) | damage meter sums stat rows 0..3: heroes 5-8 are missing from meter totals. No crash |
| CharacterWindowUI.cs:916 | hero (UI) | controller "next character" wraps after 4 |
| CardItem.cs:4355-4389 | hero (UI) | 4 hero portraits on a card |

### Out-of-combat screens (phase 1 only needs "no crash"; not verified in game)

| Screen | What heroes 5..8 get |
|---|---|
| Hero selection | 0.2.0: a box per hero (see 0.2.0 changes) |
| Map side portraits (SideCharacters, `charArray` of 4, loops use `charArray.Length`) | ignored, only 4 shown |
| Rewards (RewardsManager.cs:20, 233, 661, 676; card choices are rolled for all `theTeam.Length` heroes at :495) | 0.2.0: a row per hero (see 0.2.0 changes). XP/gold: see "XP and gold on the rewards screen" |
| Loot (LootManager.cs:22, 24, 52 and 8 loops of 4) | 0.2.6: a second row of panels (see 0.2.6) |
| Map corruption rewards (MapManager.cs:445-546 `n < 4`) | 0.2.16: every hero (see 0.2.16) |
| Events | 0.2.3: roll portrait per hero, effects for every hero, odds by convolution, group rolls scaled (see 0.2.3 changes) |
| Town craft/corruption (CardCraftManager.cs:244, 256, 909, 928 arrays of 4 indexed by `HeroIndex`, e.g. :5113) | only reachable if the town UI lets you select hero 5..8; it shows 4, so probably unreachable. Would crash if it happens |
| End of run (FinishRunManager.cs:104, 661-671), Tome history (TomeManager.cs:1132) | 0.2.5: a panel per hero, progress for all; run history keeps 4 (see 0.2.5) |
| Save slot metadata (GameData.cs:486-501) | portraits of heroes 0..3 |

## Saves

- `GameData` stores the team as a JSON array (`teamAtO = JsonHelper.ToJson(team.ToArray())`, GameData.cs:440) and loads it with
  `AssignTeamFromSaveGame` -> `GameTeam.SetHeroes` (AtOManager.cs:3155), so the team length comes from the JSON: 8-hero saves
  load with the mod. `combatStats` (int[,]) and `craftedCards` go through BinaryFormatter / the patched growers and keep any size.
- Mid-combat resume uses the sync code decoded by `FixTOTALCo` (patched). Safety net: if that transpiler is skipped,
  `SetLoadTurn` is blocked for expanded teams so the combat restarts from the start instead of loading scrambled state.
- **Risk:** an 8-hero save opened without the mod (or by a vanilla friend) loads the team but breaks in the first combat
  (`GenerateHeroes` index out of range, hero team of 4). Keep backups; the mod does not make saves vanilla-compatible.

## Known broken / unknown (phase 1)

- Not run in game yet. Most likely trouble: something not on this list indexes a 4-sized array with hero 5..8 (UI first).
- Heroes overlap visually; clicking between two overlapping heroes may pick the neighbour. Keyboard target numbers only go
  to 9, so with 12 characters some NPCs have no number key.
- Combat log loop is patched but the combat log UI may only lay out 8 entries (unchecked).
- Damage meter, rewards, loot, events, town and end-of-run screens ignore heroes 5..8 (see table).
- Extras get no skin/cardback choice (default active skin), and their perks come from the profile's saved perk setup for that
  hero, like a picked hero.
- 2x XP/gold economy because rewards are per hero (0.2.0: per-hero XP is vanilla, total gold scales with team size; see "XP and gold on the rewards screen").
- Only Adventure mode. Obelisk Challenge, weekly, Singularity, sandbox "fewer heroes" untested and skipped.

## Phase 2 plan (single-player run, proper UI)

Done in 0.2.0: item 1 (hero selection) and the rewards part of item 2. 0.2.3: events. 0.2.6-0.2.9: loot. Still open: map portraits (next), town, damage meter, character window hero switching.

1. Hero selection for 8: clone the 4 `boxGO` slots at runtime into a second row, extend `boxHero`/`boxSelection`/`boxFilled`
   dictionaries and the `string[] array = new string[4]` in `BeginAdventure` (HeroSelectionManager.cs:2544, 14 loops of 4 in that
   file), `PlayerManager.LastUsedTeam`. Drop the `[Spike] ExtraHeroes` shortcut.
2. Out-of-combat screens: clone UI rows for `SideCharacters.charArray`, `RewardsManager.characterRewardArray`/`cardSelectedArr`,
   `LootManager.characterLootArray`/`characterPotraitArray`/`looted`, `EventManager.characterT`/`characterTcards`/`characterSPR`,
   `CardCraftManager.ItemCorruptionIcons`/`corruptionTry`, `CharacterMeter` rows, and transpile their loops the same way.
3. Rebalance rewards (XP/gold `/ 4` -> `/ Count`?) and decide with EnemyScaling defaults.
4. Better combat layout (two staggered rows?) and collider overlap handling.
5. Saves: maybe store a marker so a vanilla game refuses 8-hero saves cleanly.

## Phase 3 plan (multiplayer)

1. Room size: `NetworkManager.CreateRoom` (NetworkManager.cs:402) parses `roomPlayers` (byte) — allow up to 8 players; lobby UI.
2. Ownership: `PlayerHeroPositionOwner` sized to Count (NetworkManager.cs:472, 1561), hero box assignment RPCs, `InitGameMP`.
3. RPCs with 4 fixed slots: `NET_SaveCardDeck(type, a, b, c, d)` (sent by `NET_ShareDecks`, MatchManager.cs:2128) needs a new
   variable-length RPC (e.g. one call per hero or a packed array); NPC deck share can stay.
4. Desync check: `CardNamesForSyncCode` is already patched; `FixCodeSyncFromMaster` (MatchManager.cs:15377) needs the same
   treatment as `FixTOTALCo`; `GenerateSyncCodeForCheckingAction` should be reviewed.
5. Every client must run the mod with the same HeroCount; publish it as a Photon room property like EnemyScaling does and
   refuse to start if a client lacks it. `HeroSlots.Count` currently returns 4 in MP; that guard comes off here.

## Maintenance

- Regenerate the IL dump: `ilspycmd -il Assembly-CSharp.dll > il.txt`, then count `ldc.i4.4` / `ldc.i4.s N` per method
  (a small script in the session scratchpad did this). The offline transpiler check and the UnityPy scripts now live in
  `tools/` (see HANDOFF.md). If the game updates, the startup log lists every method whose IL no
  longer matches; recount just those.

## 0.1.1 changes (after first in-game test)
- First test: 8 heroes appeared in combat. The rewards screen hung after picking 4 rewards: `AtOManager.FinishCardRewards` loops over the whole team but `RewardsManager.cardSelectedArr` has 4 entries, so hero 5 threw IndexOutOfRange inside the `CloseWindow` coroutine. The log showed nothing because the game disables Unity logging in release builds (`Globals.Awake`).
- Fix: Prefix on `FinishCardRewards` pads the array; heroes 5..N get the first card from `RewardsManager.cardsByOrder[i]` (already rolled and shuffled for every hero). Phase 2 should replace this with reward rows for every hero.
- `Diagnostics.cs` re-enables Unity logging for warnings, errors and exceptions (`[Logging] ShowGameErrors`).
- Stagger: with more than 4 heroes, even positions move up and odd positions down by `[Layout] HeroStagger` (0.4), via a `CharacterItem.CalculateLocalPosition` Postfix. The `DrawOrderSprites` Prefix draws the upper row over the lower row so HP bars and curse icons at an upper hero's feet stay visible (the user asked for this).

## 0.1.2 changes (second in-game test)
- Stagger 0.4 was not enough: the lower heroes' shield/HP bar covered the upper heroes' curse/aura icons. Those are separate sprites layered by the prefab, so body draw order cannot fix it; only vertical distance can. Default stagger is now 0.7.
- Drawing the upper row on top hid the lower heroes' bodies (they only showed when active, because the active hero is drawn with goToFront). Reverted: the lower row is now drawn over the upper row.
- `LayoutTuner.cs`: F3/F4 spacing, F5/F6 stagger, live in combat, saved to config. Each change logs the screen extent and the y/x bounds of body, HP bar, buffs, block and shield for the first two heroes. Use these numbers to set the defaults.

## 0.1.3 layout defaults (third in-game test)
- User tuned with the hotkeys and settled on stagger 0.9 with automatic spacing (0.91); lower heroes then fit between the upper heroes' HP bars and don't cover their auras/curses. Stagger default is now 0.9.
- Measurements (8 heroes, scale 0.8): screen y -5.4..5.4 (half width about 9.6); a hero body spans about -1.3..+1.4 around its x; the HP bar and buffs row sit 0.8..0.9 below the hero's y. The back hero at x -8.6 was almost off screen, so the front is now 2.0 and the back limit is half width - 1.5 (about 8.1), giving spacing about 0.87.
- Known cosmetic issue: aura/curse icons are slightly transparent (vanilla), so they can be hard to read over a hero standing behind them.

## 0.2.0 changes (phase 2 part 1: rewards rows, hero selection boxes). Not yet run in game.

### Rewards screen (`RewardsUi.cs`)
- Scene (read with UnityPy from `level11`, the Rewards scene): 4 inactive `CharacterReward` rows under `Rewards` (parent at y 0.15,
  scale 0.95), local x -0.2, y 2.6 / 0.4 / -1.8 / -4.0. In a row: deck button at x -7 (placed by code), portrait -6, gold/XP -4.4,
  cards at -1.9 + 1.95 i (scale 0.8, up to 4), dust option at x 6. Camera ortho size 5.4 (screen 19.2 x 10.8 at 16:9).
  `RewardsMultiple` (`level22`, town minigame rewards, `RewardsManagerMultiple`) has the same layout and inherits `Start`.
- Prefix on `RewardsManager.Start`: with `HeroSlots.Count` = N > 4, clone row 4 (still inactive and untouched) N-4 times under the
  same parent, grow `characterRewardArray` to N, lay all rows out. Postfix: grow `cardSelectedArr` (Start sets `new string[4]`) to
  one entry per row.
- Layout: column-major, heroes 1-4 in column 1, 5-8 in column 2 (`[Layout] RewardColumns`, default 2). Rows of a column are centred
  on the vanilla block with vanilla spacing 2.2 (up to 4 per column), so with 2 columns each row sits at its vanilla height, just
  scaled. Scale (auto) = min(1, column width fit, spacing fit); the width fit assumes a row spans 15.4 row units with 0.6 world units
  margin per side, which gives about 0.62 at 16:9 (0.56 at 16:10). Keys: `RewardColumns`, `RewardRowScale`, `RewardRowSpacing`,
  `RewardColumnSpacing` (world units), `RewardOffsetY`. One column of 8 also works (rows 0.94 apart, scale about 0.43).
- Logs: "Rewards: N rows in C column(s)..." with every row's position and expected extent when the screen opens, then
  "Rewards layout check" 3 s later with the real renderer bounds of each row, flagged `OFF SCREEN` / `overlaps previous row`.
- Transpilers (shift = number of extra rows that exist, `RewardsUi.Shift`, so a failed clone keeps 4): `ShowRewardsCo` (i < 4),
  `SetCardReward` (i < 4), `ControllerMovement` (i < 4). `CheckAllAssigned` already uses `numRewards`.
- Prefix on `CheckAllAssigned`: rows appear 0.15 s apart and the check only covers rows shown so far, so a fast player could close
  the screen before rows 7-8 appear. It now waits until every row that will be shown is shown.
- Clicks on clones: cards are named `card_<id>_<heroIndex>_<pos>` and looked up by name in each row, the deck button uses
  `auxInt = index` (both set by `CharacterReward.Init(i)`), the dust button finds its row through `transform.parent.parent`.
  `CharacterWindowUI.Show("deck", i)` uses `team.GetHero(i)`. So clones report the right hero.
- `TeamPatches.FinishCardRewardsPrefix` is now a safety net only: it fills in heroes 5..N that have no pick (array too short or
  null entry) and logs a warning. It should not trigger.
- Not handled: `NET_ShareRewards` (4 fixed card arrays) and the multiplayer paths; `HeroSlots.Count` is 4 in multiplayer.
  Possible overlap: the corruption reward panel (top right, `CorruptionRewards/bgCard` at x 8.09) can cover the top row of the
  second column when a corruption reward is shown.

### XP and gold on the rewards screen
- `experienceEach = GetExperienceFromCombat() / 4` and `goldEach = GetGoldFromCombat() / 4` (RewardsManager.cs:588) are per-hero
  shares; each row's `CharacterReward.Show` grants `experienceEach` (through `CalculateRewardForCharacter`) to its hero and gives
  `goldEach` to the owner. So every hero gets the vanilla per-hero XP whatever the team size (levelling speed unchanged), and total
  gold scales with the number of rows: 8 heroes = 2x gold per fight. Not changed: per-hero XP is what a hero needs to level, and
  twice the heroes also means twice the cards to buy/upgrade. Revisit with EnemyScaling (gold divisor `Count` would be a one-line
  transpile of the two 4s at :588/:589).
- In 0.1.x heroes 5-8 had no row, so they got **no XP and no gold** at all (the stopgap only added a card). 0.2.0 fixes that as a
  side effect of the rows.

### Hero selection (`HeroSelectUi.cs`)
- Scene (`level4`): `RightBlock/BoxCharacters/Boxes/Box_0..Box_3`, right to left (Box_0 = front hero), 1.63 apart (local), at
  world y about 2.47, a box about 1.3 x 1.9 world. The space below them only holds multiplayer widgets (Follow, MasterDescription,
  ready button), the hero roster starts around y -1.4.
- Prefix on `HeroSelectionManager.Start` (before `StartCoContinue` builds `boxSelection`, `boxHero`, `boxFilled` from `boxGO`):
  in single-player Adventure (not weekly, Obelisk, Singularity, tutorial/first game, combat tool, loading) clone `Box_k` into
  `Box_{k+4}` placed `[Layout] SelectionRowOffsetY` (2.1) below it (`SelectionRowOffsetX` 0) and append to `boxGO`. Box ids come
  from the name (`BoxSelection.Start`, `FillBox`, `IsYourBox` parse `Box_N`), so the clone is renamed before its Start runs.
  Box count = min(HeroCount, unlocked and owned heroes); fewer heroes than HeroCount -> warning, fallback tops up at Begin.
- Transpilers (shift = `boxGO.Length - 4`, `HeroSelectUi.Shift`): `AllBoxWithHeroes` (filled == 4, the Begin check),
  `IsHeroSelected` (i < 4), `SetRandomHero` (count loop, available <= 4, filled == 4), `BeginAdventure` (`new string[4]` #0
  only; `LastUsedTeam` stays 4), `HideCharacterArrowController`, `ControllerMovement` (outer box loop #1; #0 is the 4 player-name
  slots of a box), `PerkTree.DoTeamPerks` (single-player loop #0; #1 is multiplayer).
- Prefix on `SetRandomHero`: return if no hero is available (the dice loops until it finds a free hero; with 8 boxes and few
  unlocked heroes it could spin forever).
- Postfix on `RefreshCharBoxesBySandboxHeroes`: sandbox "total heroes" 1..3 empties and hides the extra boxes.
- Postfix on `PreAssign`: fills the extra boxes from `[Selection] LastExtraHeroes` (saved at Begin), or `[Spike] ExtraHeroes`
  the first time. `PlayerManager.LastUsedTeam` stays at 4 entries so the profile save stays vanilla-compatible
  (`PreAssign` ignores a team that is not exactly 4).
- Begin: `BeginAdventure` passes N subclass ids to `PerksManager.SetPlayerPerks` (handles any length) and to
  `AtOManager.SetTeamFromSubClasses`, which can only take 4 (`GameTeam` starts with 4 slots, `SetHero` does not grow). Prefix
  (`ExtraHeroes.SetTeamPatch`) passes the first 4 and keeps the rest; the Postfix creates them with `CreateHeroBySubClass` (perk
  rank, skin and cardback from the box, perks from `SetPlayerPerks`) and appends them. Then, only if the team is still short of
  HeroCount, the 0.1.x top-up runs with a warning. Logs: "Run begins, picked heroes per slot", "Run team (N heroes)".
- Logs: "Hero selection opened/ready: N boxes ..." with each box's world position and sprite bounds, the Begin button and the
  roster bounds.

### Verified offline vs. not
- Offline (harness in the session scratchpad, `ilcheck/`): all 28 transpilers apply to `PatchProcessor.GetOriginalInstructions`
  of the real game DLL, and the inserted calls sit after the intended constants (checked for BeginAdventure, ControllerMovement,
  DoTeamPerks, SetRandomHero). Scene positions are read from the game's scene files with UnityPy.
- Not verified: everything visual (row scale, box positions, overlaps), cloned rows' and boxes' behaviour (animations, hover, card
  dealing), the async pre-fill, and the begin flow with picked extras. Prefix/Postfix binding could only be partly checked offline
  (the harness cannot patch Unity methods outside the engine).

### Modes left at 4 and what would break if they were expanded
- Weekly challenge: heroes are fixed (`Hero1..Hero4`, assigned to `boxGO[0..3]`; `AssignPlayerToBox` maps box ids 3+ to Hero4).
- Obelisk challenge / Singularity: `ExtraHeroes` skips non-Adventure runs; boxes are not cloned, so 4 heroes. Perk setup there
  uses `SetPlayerPerksChallenge` over the team (fine for N).
- Tutorial / first game: assigns 4 fixed heroes and begins at once.
- Sandbox "total heroes" 1..3: extra boxes hidden, team stays at the sandbox size.
- Combat tool: not expanded (its own `LastUsedTeam` in `PlayerDataCombatTool`).

### Phase 3 (multiplayer) notes from this work
- Rewards: `NET_ShareRewards(cards0..cards3, ...)` sends 4 fixed arrays; needs a variable-length RPC. `MASTER/NET_CardSelected` and
  `NET_DustSelected` already carry the hero index.
- Hero selection: `DrawBoxSelectionNames`, `SetPlayersReady`, `CheckButtonEnabled`, `AllBoxWithOwners`, `AlfaPreAssign` loop 4
  boxes; `NetworkManager.Owner0..Owner3` and `PlayerHeroPositionOwner[4]`; `NET_AssignAllHeroToBox` indexes `boxGO[m]` by team slot;
  `BoxSelection.boxPlayer` has 4 player-name slots (4 players). The clone approach works for boxes, but every client must clone
  the same number before `StartCoContinue`.

## 0.2.1 hero selection layout (first test of 0.2.0)
- At full size the second row of boxes overlapped the hero roster (roster top about y -1.5; header text bottom about y 3.6).
- `HeroSelectUi.ApplyLayout` (run when the screen settles) scales all boxes by `SelectionBoxScale` (0.72), puts row 1's top at `SelectionTopY` (3.3) and row 2 one box height plus `SelectionRowGap` (0.5) lower. `HeroSelection.selectedPortraitScale` (1.3, the fixed size of a picked portrait) is scaled too, and portraits already in boxes are re-snapped to their box. Name/rank labels are never repositioned in code, so they should be children of the portrait sprite and follow it.
- 0.2.2: config is re-read when hero selection or rewards opens (`Plugin.ReloadConfig`). Selection defaults tuned from a screenshot: `SelectionTopY` 3.15, `SelectionRowGap` 0.15.

## 0.2.3 changes (events; second test of 0.2.x)

### Logging gotcha
- With `[Logging] ShowGameErrors` the game's own warnings/exceptions are logged again, but they go to Unity's **Player.log**
  (Linux `~/.config/unity3d/Dreamsite Games/AcrossTheObelisk/Player.log`, Windows `%USERPROFILE%\AppData\LocalLow\Dreamsite Games\AcrossTheObelisk\Player.log`;
  the previous session is `Player-prev.log`), not to `BepInEx/LogOutput.log`. Player.log also mirrors the BepInEx lines, so it is the
  one file to ask for.

### Bug 1: events froze, then showed a blank book
- Player.log: `OutOfMemoryException` in `ProbabilityContainer.AddAllSums` <- `SetProbability` <- `EventManager.SetEvent`.
  `SetEvent` (EventManager.cs:289) passes the whole team to the container; if any reply has `SsRoll`, `SetProbability` enumerates
  every combination of one card per hero (decks capped at 50) into a `List<int>`. 15^4 = 50k entries with 4 starting decks,
  15^8 = 2.5 billion with 8: out of memory inside `SetEvent`, so the rest of the event (replies) never ran.
- Fix (`EventProbability.cs`): Prefix on `SetProbability`: with more than 4 hero decks (or more than 50^4 combinations) build the
  distribution of the card-cost sum by convolution, hero by hero (probabilities, a few dozen buckets, well under 1 ms), leave the
  vanilla list empty and skip the enumeration. Prefix on `GetProbability` reads the distribution: share of sums meeting the
  target, then vanilla's `Clamp(Ceil(p * 100))` (with a 1e-7 epsilon so exact values are not rounded up). `tutorial_2` still 100.
  Differences from the enumeration: the whole deck is used instead of the first 50 cards (the actual roll draws from the whole
  deck), and a card missing from `Globals.CardEnergyCost` uses its card data cost instead of 0 (also what the roll uses).
  4 heroes: both methods untouched.
- Verified offline: a Python port of the convolution and of the rounding matches the exact enumeration for 300 random 5-6 hero
  teams, every target 0..24, both modes, scaled and unscaled (0 mismatches).
- Log: "Event odds: N hero decks, ... built in X ms, K sums" and "Event 'id' with N heroes: ... rolls: ...".

### Events for N heroes (`EventsUi.cs`)
- Event prefab (MapManager.eventPrefab = sharedassets10.assets path 73, read with UnityPy + TypeTreeGeneratorAPI for the
  MonoBehaviour fields): `book` at x 1, `book/characters` at (0.2, 1.5), `char0..char3` at x 5.6/4.1/2.6/1.1 (char0 = hero 1, right),
  scale 0.9, children `cards`, `num`, `resultTitleKO`, `resultTitleOK`. Selected reply ends at the top of the right page
  (about y 2.6..3.7), result text box about y -3.4..-1.1, continue button y -4.1.
- Postfix on `EventManager.Awake` (prefab instantiated per event): clone `char3` for heroes 5..N, grow `characterT`,
  `characterTcards`, `characterSPR`, `characterTnum`, `cardKO/cardOK.charTresult` (readonly, set by reflection) and
  `EventResultManager.CharWinner` (readonly bool[4], set by reflection) to N, wired like Awake. Layout: rows of 4, heroes 5..8
  under 1..4 in the same columns, scale 0.55 of vanilla, first row 0.5 higher than vanilla, rows 1.6 apart
  (`[Layout] EventRollScale`, `EventRollRowSpacing`, `EventRollOffsetY`; config re-read when an event opens). Logged at Awake
  ("Event: N roll portraits ...") and once per event when the roll is evaluated ("Event roll layout check": renderer bounds of
  each portrait, the selected reply and the result text position).
- Transpilers: `ShowCharactersRoll` (i < 4 #0; #1 is a string array index) and `RollCards` follow the portraits that exist
  (`EventsUi.ShiftRollSlots`); `FinalResolution` (per-hero XP, cards, perks, card destruction, HP, gold/dust via `GiveReward`,
  upgrades, items, item removal/corruption) follows the heroes with a CharWinner entry (`ShiftEventHeroes`); `Finish`
  `CharacterReplacementPosition < 4` follows the team size (`SwapCharacter` checks `team.Size` itself). If growing fails all of
  them stay 4 (heroes 5..8 then get nothing, as before).
- Postfix on `EventResultManager.OnResult`: sandbox "always pass event rolls" sets every CharWinner, not just 0..3.
- Postfix on `Reply.Init`: Single-roll odds popup lists every hero (vanilla lists heroes[0..3] by hand); group-roll popup gets a
  line "(N heroes: card total counts x4/N)" when scaling is on.
- Already fine for N (checked): `EventResult` single/competition loops use `charRollDatas.Length` (= heroes.Length);
  `ProbabilityContainer.GetProbabilitySingle/Type`, `Reply` hover and Character-target lookup loop `heroes.Length`;
  `FindHeroIndexBySubclass` only feeds `team.GetHero` and `SsCharacterReplacementPosition`; validators and `GameTeam.Has*` loop the
  team; `RemoveItemFromCharacterFromEvent`, `CorruptItemSlot` use `team.GetHero(i)`.
- Not changed: `EventManager.ControllerMovement` (controller: 4 map side portraits), `rollManager.Init(4)` (reallocated to
  heroes.Length before every roll), multiplayer paths.
- Economy: event gold/dust rewards are paid per hero (`GiveReward` per hero, vanilla), so 8 heroes get twice the event gold, like
  combat gold (see "XP and gold on the rewards screen"). Not scaled.

### Group rolls (balance)
- Group: one card per hero, costs summed and compared to the target (and to the critical targets). 8 heroes roughly double the
  total. 0.2.3/0.2.4 scaled the total (x4/N); 0.2.5 scales the target instead (see 0.2.5 changes), because a hidden x4/N made
  the shown cards and the shown target disagree.
- Single: each hero rolls alone against the target; per-hero outcome. No scaling needed (more heroes = more individual results).
- Competition: heroes compare cards, best (or worst) wins; ties reroll among the tied. No target, so no scaling; with 8 heroes
  ties are more likely (more rerolls) and each hero's chance to win is lower, which is inherent.
- Character: only the hero of the required class rolls. No scaling.
- Card-type rolls (`SsRollCard`): per hero / class, no sums. No scaling.

### Bug 2 (reported): deck view on the rewards screen does not show the picked card
- Clarified by the user: the DECK button works for all 8 rows; the deck view just does not include the card picked on that screen.
  Vanilla behaves the same for every row: picking only records `cardSelectedArr[i]` (`RewardsManager.NET_CardSelected` /
  `NET_DustSelected`); cards are added in `AtOManager.FinishCardRewards` when the screen closes, and the deck view
  (`DeckWindowUI.SetDecks`) reads `hero.Cards`. Not changed.
- Character window with heroes 5..8 (checked while looking): `Show(element, i)` and the deck/level/items/stats tabs use
  `team.GetHero(i)` and work for any index. Switching hero inside the window goes through the 4 map side portraits
  (`ControllerNextCharacter` wraps after 4, `ControllerMovement` loops 4): phase 2 part 2, with the side portraits.

### Verified offline vs. guessed
- Offline: all 33 transpilers/edits apply to the real DLL (harness `ilcheck/`, the 4 new loop shifts sit right after the intended
  `ldc.i4.4` before `blt`/`bge.s`); new Prefix/Postfix targets resolve (the harness cannot patch methods that call into the
  engine, same as before); probability math as above; build clean.
- Guessed (not run): portrait layout and scale (from prefab positions; portrait sprite size unknown), whether the clones animate
  and show cards like the originals, popup text fitting 8 lines, the "N heroes" popup line.

## 0.2.4 (test of 0.2.3)
- Events work with 8 heroes (no freeze). Two rows of roll portraits at 0.55 covered the result text, because the dealt roll cards are much taller than the portraits. Default is now one row of 8 across the page (`[Layout] EventRollColumns` 8, scale about 0.41, columns 1.15 apart centred on the page, `EventRollOffsetY` 0.2). `EventRollColumns = 4` gives the old two-row layout.
- `Guards.cs`: (1) `MenuSaveButton.SetGameData` threw IndexOutOfRange on an 8-hero save (portraits into `imgHero[4]`), which also aborted `PositionSaveItems` for the other buttons: this caused the white-portrait/"incompatible" save slots the user saw after the first 8-hero run (earlier wrongly blamed on vanilla). A Finalizer swallows that one exception for saves with more than 4 heroes; the portrait loop is the method's last step. (2) `SideCharacters.ShowActiveStatus` threw for heroes 5..8 when clicking their deck pile in combat (`MatchManager.ShowCharacterWindow`); skipped for indexes past the 4 side portraits so the character window still opens.
- Open: a combat card that lets a hero 5..8 look through their deck did nothing. Possibly the same `ShowActiveStatus` crash; re-test.

## 0.2.5 (test of 0.2.4)

### Resign froze on the loading screen (end-of-run screen)
- Player.log: `NullReferenceException` in `FinishRunManager.CalculateFinishRunReward` at IL 0x0f2f, the portrait loop
  `GetSpriteRendererByPosition(i).sprite = ...` over the whole team (FinishRunManager.cs:600). The getters map 0..3 to the scene's
  `spr0..spr3` / `fp0..fp3` and return null for 4+. The exception killed `StartCo` before the main menu button was enabled. The
  next loop (`GetFinishProgressionByPosition(i).SetCharacter`, `lockedSate[i]` with `lockedSate = new bool[4]`) would have thrown too.
  The same screen is used for resigning, dying (FinishCombat -> FinishRun) and winning (FinishGame -> IntroNewGame -> FinishRun).
- Scene (`level12`, read with UnityPy): `Characters` (scale 0.8, y 1.5) holds `charTL`/`charTR`/`charBL`/`charBR` at local
  (-5.25, -2.01) / (6.55, -2.01) / (-5.25, -4.85) / (6.55, -4.85), hero 1 = TL, 2 = TR, 3 = BL, 4 = BR. Each is the portrait sprite
  with a `finishProgression` child (name, rank progress bar with a SpriteMask, perk points) at x -2.2 (left column) / +2.2 (right).
- Fix (`FinishRunUi.cs`): Prefix on `FinishRunManager.Start` clones `charBL` (odd heroes 5, 7: left) or `charBR` (6, 8: right),
  grows `lockedSate` to N, and lays the N panels out in N/2 rows in the block the 2 vanilla rows used (rows 1.42 apart, scale 0.5
  with 8 heroes). Postfixes on both getters return the clones. Logged: "End of run: N hero panels ..." and, 2 s later, "End of run
  layout check" with renderer bounds. Finalizer on `CalculateFinishRunReward` (only with more than 4 heroes): if something still
  throws, log it and let the screen finish instead of hanging.
- Rank progress: vanilla grants the end-of-run progress to every hero in the team (`ModifyProgress(subclass, score, i)` loop over
  the whole array) and the player rank once, then saves the profile. The panels only display it (`SetCharacter` passes index 0,
  `ProgressCo` is unused). In 0.2.4 the portrait loop crashed before that loop, so the resigned run gave no progress and was not
  saved to the run history. Now all of it runs.
- Gold/dust payout bug found on the way: `if (num == 4)` (owned heroes == 4, FinishRunManager.cs:473) picks the single-player
  formula (0.6 x score gold, 1.0 x score dust in act 1); otherwise it pays the multiplayer per-hero share times the number of owned
  heroes (0.3 / 0.4 x score each), so 8 heroes would get 2.4x / 3.2x score. Transpiled (`ldc.i4.4` #0 of 6 before `bne.un`,
  `FinishRunUi.Shift` = run team size - 4), so a single-player run gets the vanilla payout. The other 4s are the act string and
  `Traits[4]`.
- Run history (`PlayerRun` in `runs.ato`, BinaryFormatter, read by the Tome): fixed fields `Char0..Char3`, `CombatStats0..3`, read
  from `array[0..3]` and `combatStats[0..3, *]`. Left alone: an 8-hero run is recorded with its first 4 heroes, which the vanilla
  game and the Tome read normally. `combatStats` rows 4..7 exist (grown in 0.1) and are simply not saved. Adding fields would break
  vanilla reading the file, so heroes 5..8 are not in the history. `player.ato` gets only what vanilla writes (progress per
  subclass, rank).
- Telemetry (`SendPlaysessionEnd`, act end) has no 4-hero arrays. `AtOManager` act-end progress (AtOManager.cs:6244) loops
  `team.Size`.

- Tested: the screen works with 8 heroes (Resign). At scale 0.5 the panels spanned world y -0.49..4.14, too small with the lower
  half empty. Now `[Layout] FinishRunScale` (0 = automatic, min(1, 3.2 / rows) = 0.8 with 8 heroes), rows at the vanilla
  spacing x scale, the first row's top edge where vanilla's is (a panel reaches 1.95 parent-local above its portrait's centre at
  full size). Expected: last row bottom about world y -3.0. Config re-read when the screen opens.
- Colours (vanilla): the progress bar and the progress number use `Globals.ClassColor[hero class]` (warrior red, mage blue,
  scout green, healer white/grey). Heroes at max rank show a full bar and no min/max numbers.

### "Look through your deck" card showed nothing for heroes 5..8 (not found)
- Retest with 0.2.5: Expert Tracker played by Andrin (hero 5) worked: "Deck window: type 2, quantity 7, cards to show 7, active
  hero 4, looked-at hero 4", 7 cards shown. The user had the wrong card in mind: the failing one is **Tactical Thinking**.
- Tactical Thinking (Andrin, hero 5): "Deck window: type 3, quantity 1, cards to show 0", text "You can add: 0 cards", 0 card
  objects. Card data (all three versions): `AddCardFrom = Game`, `AddCard 1`, `AddCardChoose 3`, `AddCardList` = 3 fixed cards
  (detection / songofcelerity / vigilance in the base card), place Hand. Following the IL of `CastCard`: the window is only opened
  if `ValidCardsStringInDeck` has cards, and `cardTotal` (`<indexGameBusy>5__28`) is `AddCardChoose` when it is > 0, so 0 is only
  possible if `AddCardChoose` is -1 at run time (then it is `ValidCardsInDeck.Count`, 0 for Game). Nothing found that sets it.
- Diagnostic added: `Diagnostics.CastCardTracker` (Prefix/Finalizer on `CastCard`'s MoveNext keeps a stack of running
  coroutines) and the deck window log now dumps the running CastCard's hoisted fields and the cast card's add-card fields.
  Next test: play Tactical Thinking with hero 5..8 (and, if possible, with hero 1..4 to see whether vanilla does the same).
- Result (8-hero runs, Andrin as hero 5 and as hero 3): both show 0. Dump at `TurnOn`: `_cardActive` = tacticalthinking_0, from
  Game, add 1, **choose 3**, list detection/songofcelerity/vigilance; `ValidCardsStringInDeck.Count=3`, `keyList.Count=3`,
  `cardsNum=1`, but `<indexGameBusy>5__28=0`. By the IL that is impossible: with choose 3 the field is set to 3 and the
  ValidCardsStringInDeck path does not touch it again (the only other writes set it to `ValidCardsInDeck.Count`, which needs
  ValidCardsStringInDeck to be empty). The patched IL (offline harness) differs from the original only by the two
  `HeroSlots.Shift` calls at the 4 #7 / #9 hero loops, far after this point. So either vanilla behaves differently at run time
  than the IL suggests or the patched method misbehaves at run time. Not slot-related. Next: test without MoreHeroes (vanilla,
  4 heroes) to tell the two apart.
- Without MoreHeroes the card works (3 cards, pick 1). **Cause: a HarmonyX 2.9 bug** (BepInEx 5.4's `0Harmony.dll`, read with
  ilspycmd: `ILManipulator` skips emitting a `leave` if the leave has block markers or the next instruction has, and
  `ILEmitter.BeginHandler` / `EndHandler` emit their own `leave` to the end of the exception block). Any method Harmony re-emits
  (any patch: transpiler, prefix, postfix, finalizer) loses the original target of such leaves. Here the foreach over
  `ValidCardsStringInDeck` (inside `if (count > 0) {...} else {...}`) leaves straight to the window code; re-emitted, it fell into
  the else branch, which sets `indexGameBusy = ValidCardsInDeck.Count` (0) and loops over the empty list: exactly the dumped state.
- Fix (`HarmonyFix.cs`): after all patches, every patched method in the game (`Harmony.GetAllPatchedMethods`, so the user's other
  mods' targets too) gets a last transpiler (`Priority.Last`) that inserts a `nop` between such a leave and the handler, or moves
  the end-of-block marker from a handler's final leave onto a `nop` after it. The leave then keeps its target; the emitter's own
  leave follows, unreachable. Startup summary: "HarmonyX leave fix: N patched method(s) checked, M fixed: ...".
- Offline (simulating the drop rule on the original IL; "harmful" = dropped leave whose target is not the end of its block):
  CastCard 3 -> 0, NextTurnContinue (corruption each round) 1 -> 0, KillCharacterCO (on-kill events) 1 -> 0, all other transpiled
  methods 0; no droppable leave left anywhere after the fix. So those two coroutines were also misbehaving since 0.1 in some
  branch. Methods patched only by Prefix/Postfix are covered at run time (not simulated offline).
- Note for the other mods (EnemyScaling, EncounterPicker): their patched methods are fixed only while MoreHeroes is loaded.
  The Expert Tracker "0 results" in the 0.2.4 test was most likely the same bug in a different branch of CastCard.
- Removed the CastCard state dump (`CastCardTracker`); the plain deck window log stays.
- Confirmed in game (2026-10-20): Tactical Thinking shows 3 cards with hero 5..8. Startup: "HarmonyX leave fix: 94 patched
  method(s) checked, 21 fixed" (nops added; includes other mods' targets such as AtOManager.AssignSingleGameNode, NPC.InitData,
  BattleTeamManager.CreateNPC), no failures, no exceptions in Player.log.
- The cards in the test team: Expert Tracker (Andrin's starting card: look at the top 7 cards of the target hero's deck, discard any),
  Trace (Gustav: top 5), Tactical Thinking (Andrin: pick 1 of 3 random class cards, `AddCardFrom = Game`). Card data read from
  `resources.assets` with UnityPy + TypeTreeGenerator (CardDataNew; Tactical Thinking needed a hand decoder because
  `description.relatedCards` is serialized as a string list).
- Code read: `CastCard` LookCards branch (MatchManager.cs:7994-8165: `InitiativesManager.GetIndexForChar` returns the hero index,
  deck = the target hero's `BattleCards` pile, `UIDeckCards.TurnOn(2, ...)`, cards created into `deckCardsWindow.cardContainer`),
  the AddCard branch (6880-7600: Game / AddCardList / class card lists, `TurnOn(3, ...)`), `UIDeckCards.TextInstructions`,
  `CountHero*`, `CardItem.SetCard` / `AmplifyForSelection`, `IsYourTurnForAddDiscard` (always true in single player). None of
  them index a 4-sized array or loop `< 4` with the hero index; the one hero loop (7563, add-to-hand for a target hero) is already
  transpiled. No fix made.
- Added a diagnostic (`Diagnostics.DeckWindowLog`, only with more than 4 heroes): "Deck window: type ..., cards to show ...,
  active hero ..., looked-at hero ..." with that hero's deck/hand/discard/vanish counts, and 1.5 s later what the window holds
  (text, number of card objects, their ids and positions). Next test: play the card with hero 5..8 and send Player.log.

### Group rolls: the target scales, not the total
- User feedback: "Group [5 or lower]" with 8 heroes is confusing when the shown cards add up to 12 and the mod silently counts 6.
  Now the target is scaled and the raw total is compared with it: `EventProbability.ScaleGroupTarget(target, higher, N)` =
  target x N / 4, where target already includes the madness modifier (as vanilla shows it). 8 heroes: exactly x2. 5-7 heroes:
  "X or lower" rounds down and "X or higher" rounds up. That is exactly "total x 4 / N meets the vanilla target" with no rounding,
  so it is as close to the vanilla difficulty as a whole-number target allows (the old rounded-total rule made "or higher" one
  point easier at 8 heroes: 2T-1 passed).
- Where: the IL edit in `EventResult` now replaces the group `compareByMode` call (the one after the only `ldfld totalEnergy`)
  with `EventsUi.CompareGroupRoll`; the old `stfld totalEnergy` edit is gone, so `totalEnergy` is the raw total. Prefix on
  `RollManager.isCritical` (group only) applies the same rule to the critical success / critical failure targets. Postfix on
  `RollManager.ToString(EventReplyData)` scales the roll box on the picked reply ("<=10"). `Reply.Init` postfix replaces the number
  in the reply banner and adds "(8 heroes: target 10, 5 with 4 heroes)" to the odds popup. `GetProbability` scales the target it
  is asked about and sums the raw distribution. Heroes counted: those with a dealt card (roll) or with a non-empty deck (display);
  the same in practice.
- Logged: "Event group roll: card total X from N heroes, needs LowerOrEqual 10 (vanilla target 5 x8/4 ...): success".

### Event roll row
- 0.2.4 screenshot (about 133 px per world unit): the 8 dealt cards spanned about 1220 px, the last one past the page edge (usable
  page about 980 px), and the row's centre was about 0.68 right of the reply banner's centre. The dealt card (about 2.9 x scale
  wide) is wider than the portrait, so it sets the row width.
- New single-row defaults: columns 0.9 apart, scale about 0.30 (`0.55 * 1.1 / 1.46 * 0.87 / 1.2`, card about 0.87 wide, row about
  7.2), centre local x 3.25. When a reply with a roll is picked (`SelectOptionResult` prefix, after the reply has moved to the top),
  the row is re-centred on the reply banner (`Reply.replyBg` bounds; skipped if more than 1.5 away from the default). Overrides:
  `EventRollScale`, `EventRollColumnSpacing`, `EventRollCenterX` (0 = automatic).
- "Event roll layout check" now also logs each dealt card's bounds, the whole row's span and centre, and the reply banner's span
  and centre, so the next screenshot can be checked against numbers.

### Verified offline vs guessed
- Offline: all 34 transpilers/edits apply to `PatchProcessor.GetOriginalInstructions` of the game DLL (harness `ilcheck/` in the
  session scratchpad); the FinishRun shift sits on `ldloc num; ldc.i4.4; bne.un` and the `CompareGroupRoll` call takes the
  `compareByMode(mode, totalEnergy, SsRollNumber)` stack. Build clean.
- Guessed (not run): end-of-run panel layout at scale 0.5 (positions from the scene file; portrait size unknown), whether cloned
  panels show their progress bars correctly (each has its own SpriteMask), the event row width and centring, the banner text
  replacement (relies on vanilla writing the number as `<size=+.2>N</size>`; a warning is logged if not found).

## 0.2.6 (loot screen)

### Cause
- `LootManager` (Loot scene, `level15`): `characterLootArray` (4 `CharacterLoot` panels), `characterPotraitArray` (4 small
  portraits put on a looted item or under the gold button) and `looted` are sized 4, and single player builds
  `characterOrder = [0, 1, 2, 3]` in `SetLoot`. Every loop stops at 4, so heroes 5..8 never got a turn and no loot. No crash.
- `activeCharacter = 4` means "nobody picking" (set after a pick) and `Looted` / `LootGold` return when `activeCharacter >= 4`.
- Item count comes from `LootData.NumItems` (`Loot.GetLootItems`), not the team size. Gold can be taken by any number of heroes
  (`goldSelected++`, `GivePlayer(GOLD, goldQuantity)` each time).

### Scene (UnityPy, `level15`)
- `Character` (world y 1.9, no components) holds `CharacterLoot0..3` at local x -6.6 / -2.2 / 2.2 / 6.6 (hero 1 left). A panel:
  `CharacterImg` (scale 0.6; `Activate` sets 0.7 / 0.525), `Items` = 5 `itemCircle` at y 0, x -1.55..1.65 (scale 0.8),
  `ActiveMark` at y 1.2, `CharacterDeck` button at (1.67, 1.05). Each panel has an Animator, so the mod never moves panels, only
  their parents. `Portraits/portrait0..3` inactive at y 10 (placed by code). `RestartLoot` at (-8.6, 4.12), `Title` at y 4.7,
  gold button `GoldQ/GoldLoot` (a RectTransform, position not readable offline). Item cards: `GetCardPosition((-2, -1.9), ...)`,
  scale 1.15, so their top is about y 0.

### Fix (`LootUi.cs`)
- Prefix on `Start` (single player, team > 4): clone the panel of the same column (hero 5 under hero 1) into a new parent
  `Character (heroes 5-8) MoreHeroes` next to `Character`, clone `portrait3` for each extra hero, grow the 3 arrays (built
  first, then assigned). Both row parents are scaled (`[Layout] LootPanelScale`, 0 = 0.8), the first row keeps vanilla's top edge
  (guessed 1.6 above the panel centre), the second row is `LootRowSpacing` (0 = 2.2 x scale) below; `LootOffsetY` moves both.
- Prefix on `SetCharacters`: append 4..N-1 to `characterOrder` (multiplayer never gets here with N > 4).
- Transpilers (shift = panels that exist - 4, `LootUi.Shift`; 4 if cloning failed): `Start`, `SetCharacters`,
  `ShowCharacters`, `ActivateCharacterCo` (#1; #0 is a coroutine state), `NextCharacter`, `HighLight`, `ControllerMovement`, and
  in `Looted` / `LootGold` both the `>= 4` guard and the `= 4` "nobody" value (new shape `Field`: next is `stfld`). Left alone:
  `SetLoot` (#0 hoisted local, #1 state, #2 multiplayer `IndependentLoot` loop), `SetLootOrder` / `NET_ShareLootIdAndOrder`
  (multiplayer, 4 fixed bytes), `LootTrail` (`GetChild(4)` = pet slot), `NextCharacterMP` (state), `AtOManager.GetLootCharacterOrder`
  (multiplayer).
- Postfix on `LootGold`: gold portraits are placed 0.8 apart under the gold button (room for 4); with N heroes they are
  0.8 x 4 / N apart so 8 fit in about the vanilla width.
- Logs: "Loot: N hero panels in 2 rows ..." at Start; "Loot layout check" 3 s later (each panel's renderer bounds, item cards,
  gold button, subtitle); "Loot: hero K took the gold ...".
- Balance: not changed. With 8 heroes and the vanilla item count, more heroes end up taking gold (same per-hero gold as vanilla).

### Verified offline vs guessed
- Offline: all 43 transpilers apply (ilcheck); the shift sits after `ldfld activeCharacter; ldc.i4.4` before `bge`/`blt.s`, before
  `stfld activeCharacter`, and on the panel loop in `ActivateCharacterCo`. Build clean.
- Guessed (not run): panel height (row placement and scale 0.8), whether the second row clears the item cards, cloned panels'
  animation and click (`OnMouseUp` -> `ChangeCharacter(heroIndex)`, deck button `auxInt` set by `AssignHero`), gold portrait
  positions.

## 0.2.7 (loot layout, test of 0.2.6)
- Tested: 8 panels show and animate, picks go to the right hero, gold works (portrait under the gold button at (5.65, -2.96)).
  But the second row was hidden behind the item cards: with 8 items the cards are dealt in two rows reaching y 1.18, and the
  panels are much taller than guessed. Layout check at scale 0.8: an inactive panel spans y -0.39..+2.15 around its centre
  (full size: -0.49..+2.69), x +-1.82 (full size +-2.28); the hero picking reaches +2.87 (+3.6). Subtitle centred at y 4.25, gold
  button x 4.99..6.99, y -2.39..-1.54.
- Two rows would have to be scale 0.4 to fit between the cards and the subtitle, so the default is now **one row of 8** across the
  screen (`[Layout] LootRows` 1; 2 = the old two rows, auto-scaled to fit). Auto scale = min(1, width fit, height fit): width fit
  = (screen width - 0.6) / (4.4 x (panels per row - 1) + 4.56), about 0.53 for 8 at 16:9; panels 4.4 x scale apart, centred.
  The lowest row's bottom edge sits at y 1.35 (+ `LootOffsetY`); 8 in one row: centres at y 1.61, top about 3.0 (3.5 for the hero
  picking).
- Each panel (scene and clone) is moved into its own wrapper `LootSlot<i> MoreHeroes`, which is positioned and scaled; the panel
  keeps its vanilla local transform (Animator). Nothing in the game finds the panels by path (only through `characterLootArray`).
- Guessed: whether 0.53 is readable (equipment circles about 0.3 world wide) and whether the first panel's active portrait
  touches the Restart button (top-left, about y 3.8 and up).

## 0.2.8 (loot layout, test of 0.2.7)
- 0.2.7's one row of 8 at 0.53 was too small to read, and there is no more width. User asked for smaller item cards and two rows.
- Card code: `ShowItemsForLoot` creates each card under `cardContainer` and sets `SetDestination` (= `destinationLocalPosition`)
  and `SetDestinationLocalScale` (`cardSize`, local scale 1.15); only the start position is world. So scaling and moving the
  container scales the whole card grid (and the hover enlargement) without touching the cards.
- Layout now (defaults, 8 heroes): `LootRows` 2, panels at 0.75 from vanilla's top edge (y 4.59) down: row centres y 2.57 and
  0.07, panels end at y -0.30. Card container scaled to fill the rest: (cards top - (-5.25)) / 6.22 = about 0.77, keeping the grid's
  centre column x -2 (cards about x -5.8..1.8, y -5.25..-0.45). `LootCardScale` overrides (0 = auto).
- The gold text (`description`, about y 0.1 down, x 4..8) would be under heroes 7-8: `description` and `botonGold` are moved down by
  the overlap (about 0.55). The gold portraits are placed relative to `botonGold`, so they follow.
- Guessed: whether the GoldQ / GoldLoot Animators reset the moved positions; whether 0.77 cards are readable; hero 1's
  active portrait (top row) vs the Restart button.

## 0.2.9 (loot layout, test of 0.2.8)
- 0.2.8 first showed one row: the user's .cfg still had `LootRows = 1` from 0.2.7 (BepInEx keeps saved values, a new default does
  not replace them). Set to 2 by hand. Lesson: changing a default does not reach existing installs; rename the key if it must.
- Two rows worked. User: move the bottom row up and make the cards bigger. Renderer bounds overstate the panel's top (2.69 above
  centre at full size, some invisible renderer); on screen (2000 px wide, 104 px per world unit) the tallest portraits reach about
  1.5 above centre at 0.75 (2.0 full size), the picking hero about 1.6 (Yogger). Row pitch is now 2.87 x scale (was 3.33): 2.15 at
  0.75, row centres y 2.57 / 0.42, panels end at y 0.05. Cards fill down to y -5.3 (was -5.25): scale about 0.84 (was 0.77).
  Gold text moved down about 0.2.
- Risk: a tall hero in the bottom row, while picking (portrait 1.33x), may reach the top row's equipment circles.
- Confirmed in game (2026-10-20): the user is happy with the 0.2.9 loot layout (8 items, 8 heroes, all picks landed correctly).

## 0.2.10 (side portraits)

### Cause
The column of hero portraits on the left of the map, town, events and other out-of-combat screens (`SideCharacters`) has 4
`OverCharacter` portraits. Clicking one opens the character window for that hero, so heroes 5..8 could not be inspected or
managed (deck, items, level-up, perks) outside combat, and the town was unreachable for them.

### Scene (UnityPy)
- `SideCharacters` is a root object in each scene that has the column: levels 6, 9 (Town), 10 (Map), 16, 17, 19, 21, 23-27. Its
  children are `OverCharacter0..3` (inactive in the scene; Town local y 0 / -1.2 / -2.4 / -3.6, Map 0.4 / -1.0 / -2.4 / -3.8, both
  overwritten by `Resize`), each with `ChallengeReady`, `Sprite` (scale 0.6) and `Shadows`, a BoxCollider2D and an Animator.
- The Animator (`OverCharacter` controller, clips `overCharacter` / `overCharacterHide`) animates 5 children's positions and one
  script field, never the portrait root, so the root can be moved and the column scaled.
- `Resize`: column at (-sizeW/2 + 0.39, sizeH/2 - 1.9) = (-9.21, 3.5) at 16:9, portraits at local y -1.24 * i. Called by `Show`,
  `MapManager.Resize`, `TownManager.Resize` and both challenge selection managers.
- The game finds portraits by name: `GameObject.Find("/SideCharacters/OverCharacter" + index)` in
  `CharacterWindowUI.ControllerNextCharacter` and `ChallengeSelectionManager.NextHeroFunc`.

### Fix (`SidePortraits.cs`)
- `SideCharacters.Awake` prefix (and `Show` prefix as a fallback if Awake saw 4 heroes): clone `OverCharacter3` for heroes
  5..N, named `OverCharacter4..N-1` (no " MoreHeroes" suffix, because of the Find above), grow `charArray` and the private
  `charTransforms` (build first, then assign; on failure the clones are destroyed and the column stays at 4). All
  `SideCharacters` methods loop over `charArray.Length`, so Show / Refresh / SetActive / RefreshCards etc. cover the clones.
- `Resize` postfix: scale the whole `SideCharacters` object around its anchor (portrait 1 stays put). Auto scale = min(0.8,
  fit), fit = (top - (-sizeH/2 + 0.9)) / (7 x 1.24) = about 0.92 for 8 at 16:9, so 0.8: portraits 0.99 apart, centres from y 3.5
  to about -3.44. `[Layout] SidePortraitScale` / `SidePortraitSpacing` override (0 = auto); config is re-read on every Resize.
- `MapManager.Resize` postfix: the tracked-requirements list (`trackList`, vanilla (-7.6, -1.65), tracks 0.55 apart downwards)
  is moved under the column (column bottom - 0.3, not below -sizeH/2 + 0.6) when the column reaches it.
- Logs: "Side portraits: added portraits ...", one layout line per column and a "layout check" 2 s later (renderer bounds and
  click areas per hero, the track list on the map).
- Stopgaps kept as fallbacks for a failed clone (no-ops with 8 portraits): `TeamPatches.CharacterIconPositionPrefix`,
  `Guards.SidePanelHighlightPatch`.

### Not done (controller only)
Literal 4s over `charArray` remain in controller navigation: `CharacterWindowUI.ControllerMovement` (j < 4) and
`ControllerNextCharacter` (num > 3 wraps to hero 1), `EventManager.ControllerMovement`, `CardCraftManager` (5 loops),
`ConflictManager`, `CorruptionManager`, `CardPlayer(Pairs)Manager`. With a controller, heroes 5..8's portraits are not in the
navigation list. Mouse users are not affected.

### Verified offline vs guessed
- Verified offline: the scene layout and animation bindings above, every `SideCharacters` loop uses `charArray.Length`, build
  clean, ilcheck 43/43.
- Guessed: that the team is already loaded when each scene's `SideCharacters.Awake` runs (the Show prefix covers it if not);
  that 0.8 is readable; whether the character window has other 4-hero limits for heroes 5..8 (code reading found none); the
  track list's size (moved down blindly, see the layout check).

## 0.2.11 (character window, test of 0.2.10)
- Tested 0.2.10 on the map: 8 portraits show, layout check matches (scale 0.8, 0.99 apart, hero 8 y -3.95..-2.93, click areas
  do not overlap), names/HP/level show on hover and the user finds them readable. Map track list moved to y -4.22 (0 tracks).
- Problem: in the character window (deck, character sheet...) the Exit button (local (-sizeW/2 + 1, -2), `CharacterWindowUI.Resize`)
  and on the deck tab the energy graph + "Average energy cost" (`deckEnergy`, under `DeckUI`, inside a Canvas, so the scene's local
  position (-0.05, -1.15) is anchor-relative) cover portraits 6..8.
- Fix: `CharacterWindowUI.Show` postfix (not in combat, only with more than 4 heroes) mirrors each of them to the right half by its
  renderer bounds (centre x -> -centre x, same height); only something still on the left is moved, so it is idempotent, and Resize
  putting Exit back on the left is undone by the next Show. User asked for bottom right; the right side below the tab buttons is
  empty in the screenshot.
- Guessed: TMP text bounds on the very first Show (mesh may not be built yet); whether the town's card craft screen has its own
  Exit / energy graph in the same place (not handled).

## 0.2.12 (town buildings, events, test of 0.2.11)
- Tested 0.2.11: town base screen fine. Character window: Exit and energy graph moved right, but Exit was half off screen.
  Log: the vanilla Exit button spans x -10.16..-7.06 (it sticks out past the left edge by design); mirrored it went to
  7.06..10.16. Energy graph vanilla x -9.59..-7.04, y -4.67..-2.74. Fix: the mirror now clamps the right edge to the screen.
- Town buildings (magic forge, church, altar, armory, divination cart) and event shops: `CardCraftManager.exitCraftButton`
  (local (-sizeW/2 + 1, -2.2), `CardCraftManager.Resize`) covers portraits 6..8; forge, altar and church also show
  `deckEnergy` there. Event: `MapManager.eventShowHideButton` (shown in the `DoEventCo` coroutine) covers portrait 8.
  craftType: 0 transmute (altar), 1 remove (church), 2 craft (forge, cards up to about x 8.2 on the right), 3 divination,
  4 items (armory, event shop; Shady deal on the right).
- `ShowCardCraft` is `async void` and the Hide button is shown from a coroutine, so `SideClearance.cs` (a component on the
  DontDestroyOnLoad runner) checks every 0.25 s and moves only what is off target: craft Exit and event Hide to the bottom left
  under the column (bottom edge at screen bottom + 0.15, x unchanged); energy graph mirrored right (types 0, 1) or, in the
  forge, scaled to fit the strip x 8.25..9.5 right of the card grid, bottom aligned (about 0.49). Vanilla placement of the
  graph is recorded once per scene object so every target is computed from vanilla.
- User suggested Exit on the right for most buildings; bottom left was picked because it works in all of them (forge and event
  shop included). Guessed: forge strip width (from a screenshot), whether a button Animator moves the button root (would
  jitter), Hide/Exit height (bounds about 1.0 tall incl. hover background, column ends at -3.95, slot is -5.25..-4.25).

## 0.2.13 (forge energy graph, test of 0.2.12)
- Confirmed in game (2026-10-20): church, altar, divination cart, armory, event shop (Exit bottom left; graph right in church
  and altar), event Hide button (bottom left), character window Exit on screen.
- Log: craft Exit vanilla x -10.15..-7.05, y -2.50..-1.50; craft energy graph vanilla x -9.57..-7.02, y -5.11..-2.79 (2.55 x 2.32);
  event Hide x -10.00..-7.40, y -4.30..-3.30. All moved to y -5.25..-4.25, under the column (ends -3.92).
- Forge: the graph at 0.49 in the bottom-right strip was too small and the version label covered "Average energy cost". Moved to
  the free box at the top right (x 7.46..9.5, y 3.26..4.7: right of "Search cards", under the top bar, above the cards), fit
  scale about 0.62, centred. Box from a screenshot (guessed).
- Forge saved-decks panel (`cardCraftSave` active, opened by `ShowSaveLoad`): the graph overlapped the panel's text. Hidden while
  the panel is open; shown again when it closes (or on the next altar/church/forge view if the screen was left with it open).

## 0.2.14 (forge energy graph, test of 0.2.13)
- Confirmed: saved-decks panel hides the graph; it reappears on returning, and on the next altar/church/forge after leaving the
  forge with the panel open. The short delay before the graph moves (up to 0.25 s) is accepted.
- Forge graph clipped by the top bar and too big at first, smaller (readable) after visiting the altar and coming back. Log:
  the first forge record measured y -4.82..-2.89 (1.93 tall, text meshes not built yet) -> scale 0.75; the correct vanilla size
  is 2.55 x 2.32 (altar/church record) -> 0.62. The one-off record was the bug.
- Fix: the forge placement re-measures the graph's current bounds every check (size / current scale = vanilla size), fits it to
  the box x 7.46..9.5, y 3.01..4.45 (moved down 0.25 and right-aligned per the user) and aligns its top-right corner. Each
  distinct scale is logged.
- Confirmed in game (2026-10-20): first forge load and forge -> altar -> forge both show the graph at the same readable size, clear
  of the top bar; it stays put when switching heroes. Side portraits work is done for the map, town buildings, events, event
  shops and the character window (controller navigation still not done).

## 0.2.15 (town for heroes 5..8, corruption)
- User tested the town for heroes 5..8: adding, removing and upgrading cards, buying items and pets all worked (order-dependent
  problems not ruled out).
- Corruption (craftType 6 card, 7 item): the energy graph sat over portraits 6..8 and the Exit button (types 6/7 were not in the
  mirror list). Now mirrored right like the altar and church (`SideClearance.ShowsEnergy`).
- Corruption for heroes 5..8: clicking Reginald (hero 6) showed "Reginald cards" with Malukah's deck. Player.log:
  IndexOutOfRangeException in `CardCraftManager.ShowRemainingUses` <- `CreateDeck` <- `SelectCharacter` <- `OnSideHeroClicked`.
  Cause: `private int[] corruptionTry = new int[4]` (also recreated as new int[4] in ShowCardCraft for types 6 and 7), indexed
  by `currentHero.HeroIndex` in ShowRemainingUses, CalculateCorruption, BuyCorruptionCo, BuyItemCorruptionCo.
- Fix: `Guards.CorruptionTriesPatch`, a prefix on those 4 methods that grows the array to the team size keeping its values.
  `ItemCorruptionIcons[4]` is indexed by item slot, not hero (left alone).
- Verified offline: the exception trace and every corruptionTry index site. Confirmed in game (2026-10-20): each hero 5..8 shows
  its own deck; remaining uses and chance go down only for that hero; energy graph at the bottom right.

## 0.2.16 (map corruption rewards)
- Cause: `MapManager.DoCorruptionReward` (coroutine, `<DoCorruptionReward>d__75::MoveNext`) pays the reward of an accepted
  combat corruption on the map with `for (n < 4)` hero loops: goldshards0 (80 or 130 gold and dust per hero), goldshards1 (180
  or 250 per hero, plus 1 or 2 supply once) and randomcardupgrade (one random card upgraded per hero). Heroes 5..8 got nothing.
- Fix: spec `MapManager.DoCorruptionReward`, `ldc.i4.4` x6, occurrences 0, 1, 3, 4, 5 shifted (all `blt`); occurrence 2 is a
  coroutine state (`stfld <>1__state`), left alone. The loops index `team.ToArray()`, which has a slot per hero.
- Not changed (already every hero): heal20 (`GameTeam.ModifyHeroLife(-1)` loops over `heroes`), herocard
  (`GameTeam.GetRandomHero` picks from all heroes), the shop/altar/healer rewards (town screens, 0.2.15). Supply is paid once
  per player, not per hero.
- Balance: gold/dust per hero, as in vanilla (same rule as combat, event and loot gold): 8 heroes get twice the total.
- The randomcardupgrade result list (`DeckWindowUI.ShowUpgradedCards` -> `SetList`, the normal deck grid) gets 8 cards.
- Verified offline: ilcheck applies the spec (44/44) and the 5 calls follow the 5 loop bounds. Not yet tested in game.

## 0.2.17 (rewards card size, corruption Hide button; test of 0.2.16)
- 0.2.16 test: corruption gold/dust confirmed for 8 heroes by the user; randomcardupgrade not seen yet. Startup 44/44, no exceptions.
- Rewards screen: the user could not read the cards (row scale 0.57, width-limited) and saw unused space. Measured from their
  screenshot (2000x1125, row scale 0.57 x parent 0.95 = 56.4 px per row unit): cards 1.35 wide, 2.02 tall, 1.95 apart, first card
  left edge -2.52; the dust option (ChooseDust/DustQ, RectTransforms with anchoredPosition x 6; their shared Animator clip
  `chooseCards` only animates scale, so localPosition can be moved) spans 5.2..7.0; the row's left edge is about -7.7. A row of
  3 cards (non-dual-class) left the 4th slot empty; rows are 2.2 apart in parent units, so at row scale 0.57 there was vertical
  room.
- Change (`RewardsUi`): in a `ShowRewardsCo` prefix (cardsByOrder is known, rows not shown yet) the rows are laid out again for
  the most cards any row deals (1..4). Inside each row `cardsTransform` is scaled by `[Layout] RewardCardScale` (0 = 1.3) about
  the first card's left edge, the dust option goes 0.45 after the last card, the row width is computed from that, and the row
  scale is limited by width, by height and so the scaled cards stay 0.15 apart vertically. ScreenMargin 0.6 -> 0.35.
  Expected at 16:9 with 3 cards: row scale 0.66 (was 0.57), cards 0.85 of vanilla (was 0.57). With a dual-class hero (4 cards):
  row scale about 0.56, cards 0.72.
- `SelectedCardPatch` (postfix on CharacterReward.ShowSelected / SelectMinigameCard): the picked card goes to x -4 Cards-local;
  with scaled cards it is moved so its left edge stays at vanilla's (-4.675 row units), not further into the portrait.
- Corruption screen on the map: its Hide button (`CorruptionHide`, child of `MapManager.corruption`, (-9.7, -3.3) scale 1.1)
  sat over portraits 7-8. `SideClearance` now moves it to the bottom left like the event Hide button.
- Guessed: the card hover zoom with a scaled parent (should just be bigger); whether 0.85 is readable.

## 0.2.18 (reward card spacing; test of 0.2.17)
- 0.2.17 test: rewards "a bit more readable"; with a corruption that adds a card (4 cards for every hero) the layout fit
  (log: up to 4 cards per row). User asked to bring the cards slightly closer together so they can be bigger.
- Change: custom IL edit `CharacterReward.DoCard` (`RewardsUi.EditCardPitch`): the single `ldc.r4 1.95` (next: mul) in
  `x = -1.9 + position * 1.95` is followed by `call RewardsUi.CardPitch(float)`, which returns 1.95 at 4 heroes and otherwise
  `[Layout] RewardCardSpacing` (0 = 1.6: gap 0.25 instead of 0.6, card 1.35 wide). Done in the IL rather than moving cards
  afterwards because `DoReward` records the dealt position as the card's destination. The row layout (`DustX`) uses the same
  spacing. Startup summary: 45/45 transpilers.
- Expected at 16:9 (card scale 1.3): 4 cards per row: row scale 0.61 (was 0.56), cards 0.79 of vanilla (was 0.72);
  3 cards: row scale 0.70, cards 0.91 (was 0.85).
- Verified offline: ilcheck applies the edit (45/45). Not yet tested in game.
- Confirmed in game (2026-10-20): 4 cards per row with the closer spacing, and picked cards placed next to the portrait;
  the user is happy with the rewards screen ("works well enough"). Don't redo this layout.
- Corruption screen Hide button (0.2.17): confirmed in game by the user (2026-10-20).

## 0.2.19 (combat stats screen)
- Screen: `DamageMeterManager` (scene 1 `level1`, DontDestroyOnLoad, uGUI canvas), opened from the map/combat button
  (BotonRollover) and from the Tome of Knowledge (past runs, 4 rows). `DamageMeterManager.DoStats` already loops over
  `characterMeter.Length`; `CharacterMeter.DoStats(index)` reads `combatStats[index, i]` / `combatStatsCurrent` and sums the
  totals and the best-hero highlight with `for (j < 4)` (ldc.i4.4 x4: occurrence 1 is that loop, 0/2/3 are `i > 4` column
  checks).
- Scene (offline, `rtree.py`/`mbraw.py` in the session scratchpad: RectTransform fields and MonoBehaviour script names read
  from raw bytes, because level1's MonoBehaviours have no typetree): Panel/Elements (scale 0.9, GridLayoutGroup +
  ContentSizeFitter)/Group (GridLayoutGroup, cell 960 x 140, 1 column, no spacing/padding) with children Header (LayoutElement),
  CharacterMeter .. (3) (pivot top, LayoutElement), Total. A row's Content is centred in the row and its portrait/texts sit 70
  below Content's centre, so row i shows on the line between cells i and i+1. The background sprite `background-stats`
  (2560x1436) has the 5 row lines painted in (under the header and under each row); no line-free version exists.
- Change (`DamageMeterUi`): Show prefix; rows cloned from row 4 as needed (kept, the manager never dies), `characterMeter` set to
  the first n rows, n = rows of combatStats/combatStatsCurrent clamped 4..8 (Tome: 4 -> vanilla restored). With n > 4: two
  rows per band between the painted lines (grid cell 70, padding top +140, Header and Total `ignoreLayout` at their vanilla
  positions), so row i is centred at vanilla -245 - 70 i (band centre +-35). Portraits scaled 0.52, texts 0.85 about their own
  centres (Detailed/Icons children individually) so columns stay under the header. `[Layout] MeterPortraitScale`,
  `MeterTextScale` (0 = auto). Spec `CharacterMeter.DoStats` shifts the j < 4 loop with `DamageMeterUi.Shift` (rows shown).
  Log: "Combat stats: N rows ..." and a "Combat stats layout check" with each portrait's world y/height and the first value.
- Verified offline: ilcheck 46/46, the call is on the loop bound. Guessed: that the row elements end up where the scene data
  says (the vertical offset analysis matched the user's 4-row screenshot: rows 131 px apart, header text 280 px, row 1 387 px);
  that GridLayoutGroup re-lays out with the new padding (MarkLayoutForRebuild is called); readability at 0.85.
- Confirmed in game (2026-10-20): combat stats in a fight and on the map show 8 rows, two per band, all readable ("looks
  fine"); totals include heroes 5-8 (82 = 6 + 51 + 25, heroes 3, 5, 6) and the best-hero highlight went to hero 5 (51).
  Layout check (canvas px): rows 84 apart, portraits 75 tall, totals 98 under row 8. No exceptions.
  Tome of Knowledge "Combat stats" tab for a past 8-hero run: vanilla 4-row layout, heroes 1-4 with their saved stats
  (confirmed 2026-10-20; heroes 5-8 are not in the run history by design).
- Map corruption "free card upgrade" (altar with one free use): works for all 8 heroes (user screenshot, Andrin = hero 5).
  `randomcardupgrade` (one random card upgraded per hero, 0.2.16 loop shift) still not seen.

## 0.2.20 (card minigame portraits)
- User test: the "cards are shuffled, choose one" event minigame (CardPlayerManager) showed only heroes 1-4 under the picked
  card. The reward itself already reaches every hero (FinishSelectionCo loops `team.Size`; its 4 `i < 4` loops are over the 4
  cards). Cause: `CardItem.portrait` is a SpriteRenderer[4] (2 x 2 grid under the card; one row of 4 after
  `AlignPortraitsHorizontally` in the town minigame) and `ShowPortrait` fills the first free slot of 4, dropping the rest.
- Fix (`MinigamesUi.ShowPortraitPatch`, prefix on ShowPortrait, only with more than 4 heroes and only from the 5th portrait):
  slots grown with clones of the 4th, vanilla positions of slots 1-4 recorded, then 2 x 2 -> two rows of ceil(n/2) scaled
  2.6 / columns (0.65 for 8; top-row y kept, heroes 1-4 on top), or a row of 4 -> more rows of 4 below. Log line
  "Card minigame: N hero portraits on card ..." with the vanilla slot positions.
- Not changed, noted for later: CardPlayerPairsManager (pairs memory game) builds `orderArray = new int[4]` from heroes 0..3
  (Start, :126-147), so only heroes 1-4 take turns there; its ShowPortrait(_, 1) shows one portrait per card. Changing the
  number of turns is a gameplay decision (ask the user).
- Guessed: slot spacing (from the screenshot about 0.9 world units across, 1.4 down); verified only by build.

## 0.2.21 (card minigame portraits, test of 0.2.20)
- 0.2.20 test: 8 portraits in 2 rows of 4 at the planned positions (log: slot spacing 0.82 x 1.25 local, positions x 0.65),
  but drawn at full size, so neighbours overlapped (screenshot: 62 px apart, about 70 px wide). The slots' own localScale was
  set but did not show; probably an Animator on the portrait (pop-in) drives it (not verified offline).
- Change: the shared parent `CardItem.portraits` is scaled by s (0.65) instead, slots keep their scale, and their positions are
  divided by s so the layout is the same as planned.
- Confirmed in game (2026-10-20): 8 small portraits in 2 rows of 4 under the picked card, no overlap ("looks good"). Scaling
  the parent works, which supports the guess that the slots' own scale is animated. Town-minigame row mode still untested.

## Card minigame events (offline survey, 2026-10-20; tools/unitypy/minigame_events.py, event_nodes.py)
- Pairs game (CardPlayerPairsManager): e_pyr3_a 'The ancient machine' (node pyr_4 'Machine', zone Pyramid, store sku 2511580),
  e_sahti22_a 'The great pirate party' (node sahti_22 'Village', zone Sahti, sku 3185630); e_challengelow_14 /
  e_challengehigh_12 (Obelisk Challenge only). No event chains into them.
- How it plays: maxRounds 8, a round is 2 flips (currentRound counts flips), the flipping hero is
  theTeam[orderArray[currentRound % 4]], so 16 flips rotate over 4 heroes (4 each); pairs go to the hero who found them
  (GiveCardToHero). charSpr[4] shows the turn order. With 8 heroes, rotating % N would give each hero 2 flips with the same
  total number of flips (same total rewards, spread over all heroes); charSpr would need 8 portraits.
- Shuffle game (CardPlayerManager): 15 events, e.g. e_faen30_a 'Run or freeze!' (seen in game), e_forge4_a, e_sewers5_a,
  e_ulmin7_a, e_sahti58_a, e_aquar39_a/40_a, e_faen20_a/35, e_sunken22_a, e_ulmin9_a, e_velka30_a, Zingara (challenge).

## 0.2.22 (pairs memory game: every hero takes turns)
- User test (e_sahti22_a, "Free drinks? ... drinking contest!"): only heroes 1-4 took turns, as predicted. User asked for all 8.
- Change: 7 specs on CardPlayerPairsManager (Start, NextRound, SetCharacterOrder, ShowCurrentCharacter, ShowPlayerName,
  CanClick, SelectCardCo). Turn-slot sites (`% 4` -> new Shape.Rem, slot loops, `new int[4]`, the pad-to-4 checks) call
  `MinigamesUi.PairsShift` (slots = team size rounded up to even: the second flip of a pair reads slot `currentRound % N - 1`
  and the order is reshuffled when `currentRound % N == 0`, so an odd N would reshuffle between the two flips and read slot
  -1). The two loops over theTeam (Start i < 4, SelectCardCo's pair-to-hero match by portrait sprite) follow the team size.
  New: `SiteSpec.ShiftWith` (per-site shift, `S(MethodInfo, ...)`) for methods that mix both counts.
- `MinigamesUi.PairsStartPatch` (Start prefix): charSpr (Selection/Char0..3, local x 0, 0.9, 2.1, 3.0, scale 0.6, Selection
  at world (4.8, -1.7)) grown to the slot count with clones of Char3, laid out in pairs with the vanilla spacing times s,
  where s fits from Char0's vanilla x to the screen's right edge - 0.25 with a big (0.8) last portrait; the game's spriteSmall
  / spriteBig (0.6 / 0.8, set every turn by ShowCurrentCharacter) are scaled by s too. `[Layout] PairsTurnScale` (0 = auto).
  Log: "Card pairs: N turn slots ..." and a "Card pairs layout check" with each portrait's bounds and the PlayerTurn text.
- Total flips unchanged (16), so with 8 heroes each hero flips twice (vanilla 4 x 4); total rewards unchanged.
- Verified offline: ilcheck 53/53, each inserted call uses the intended shift. Guessed: the strip layout (s about 0.6 at
  16:9; the PlayerTurn text position is unknown offline, see the layout check).

## 0.2.23 (pairs turn strip centred; test of 0.2.22)
- 0.2.22 test: every hero takes turns, and a pair found by heroes 5-8 went into their deck (user confirmed). The turn strip
  was off centre: layout check x 4.60..9.28 (Char0 kept at its vanilla x, growing right) while the "Round / turn" text is
  centred at x 6.30, which is also the centre of vanilla's 4 portraits (Selection 4.8 + (2.1 + 0.9) / 2). A small portrait
  measured 0.40 wide at scale 0.36 (0.66 at the scene's 0.6; 0.2.22 assumed 0.84).
- Change: the strip is centred on the vanilla centre, scale = the smaller half-room (panel 3.85..9.0 at 16:9, from the
  screenshot) / (half span + half a big portrait). Expected s about 0.61 (same size as 0.2.22), strip about 3.85..8.75.
- Confirmed in game (2026-10-20): the strip is centred under the turn text; pairs split across heroes 1-4 and 5-8 give the card
  to both (Drunk: Yogger 1 + Zek 7; Well Fed: Reginald 2 + Andrin 5), including after reshuffles (round 7 of 8). No exceptions.

## Conflicts, Blackjack, High/Low and town minigames (offline survey, 2026-10-20)
- Conflict screen (ConflictManager): multiplayer only. MapManager starts it (NET_DoConflict) when players pick different map
  nodes; no event uses it. `AtOManager.GetConflictResolutionOrder` builds an order of 0..3 (phase 3 concern).
- Blackjack / High/Low (scenes 26 BlackjackGame, 27 HighLowGame): only started by `AtOManager.DoBlackjackGame/DoHighLowGame`,
  which are only called by the restart buttons inside those games. `GetMinigameKey("Senenthia")` is "Blackjack", but
  `TownMinigameManager.ZoneHasMinigame` / `StartGame` have no Senenthia case; High/Low has no zone. Unreachable in this build.
- Town minigame building (supply boxes, `TownMinigameManager.StartGame`, sets StartedTownMiniGame): Velkarath = betting game,
  Aquarfall / Faeborg = card sequence memory, Sahti = pairs game (town version of CardPlayerPairsManager), Voidhigh / Voidlow
  / WitchWoods / CastleSpire / CastleCourtyard = shuffle game (CardPlayerManager, town row layout of portraits), Ulminin =
  spot the difference. None tested with 8 heroes.
- Town shuffle game (Void town, 'honorable'): confirmed in game (2026-10-20); 8 portraits as two rows of 4 (town row mode:
  vanilla row at x -0.82..1.64, second row 1.04 below), all 8 heroes got the card.

## 0.2.24 (co-op hook for MorePlayers; built, not run in game)

The user wants co-op with up to 8 players, as a separate mod (`../MorePlayers`, see its NOTES.md for the co-op survey and
design). MoreHeroes keeps no co-op code; it only lets MorePlayers switch its patches on in a co-op room:
- `MultiplayerApi` (public): `RoomHeroCount` (set by MorePlayers to the room's hero count), `Current()` (4 without
  MorePlayers or if the callback throws), `Expanded`, `ConfiguredHeroCount` (HeroCount, or 4 with Enabled=false).
- `HeroSlots.Count`: in co-op the team size when `MultiplayerApi.Expanded`, otherwise 4 as before.
- `HeroSelectUi`: co-op gets the room's box count (the same on every client; not limited by the local profile's unlocked
  heroes, each player picks from their own) and boxes are added when loading a co-op save too (the game assigns the saved team
  to `boxGO[m]` for every hero). Single-player loading still keeps 4 (the screen is not used).
- `ExtraHeroes`: co-op allowed when expanded, target = room count; in co-op no fallback top-up (a hero from the host's config
  would have no owner), perks of a hero missing from `PerksManager.heroPerks` stay empty instead of the host's profile perks,
  and co-op picks do not overwrite `[Selection] LastExtraHeroes`.
- `FinishRunUi.RunHeroes`: co-op allowed when expanded.
- `HarmonyFix` is public; `ApplyToAllPatchedMethods` skips methods that already carry the fix, so MorePlayers calls it again
  for the methods it patches.
- Tools: `tools/ilcheck` takes `NS=MorePlayers` (and resolves MoreHeroes.dll next to the mod); `tools/ilm.py` prints a method's
  constants with the next instruction; `tools/unitypy/findname.py` finds prefabs in sharedassets files.

Verified offline: builds with 0 warnings, 53/53 transpilers still apply. Without MorePlayers nothing changes (RoomHeroCount null
-> co-op stays at 4).
