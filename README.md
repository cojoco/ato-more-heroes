# More Heroes (experimental)

A BepInEx 5 mod for Across the Obelisk that puts up to 8 heroes in a single-player Adventure run.

**Status: 0.2.24 test build.** 8-hero combat works; the hero selection, rewards, event, loot and end-of-run screens handle every
hero. 0.2.10 added side portraits for heroes 5 to 8 (map confirmed). The town is not adapted yet.

## Related mods

- [Enemy Scaling](https://github.com/cojoco/ato-enemy-scaling): stronger enemies. Required by More Heroes.
- [More Players](https://github.com/cojoco/ato-more-players): co-op with more than 4 heroes and rooms for up to 8 players. Needs More Heroes.

## What it does

- The hero selection screen shows a second row of boxes, so you pick all 8 heroes (HeroCount boxes). The extra boxes are
  pre-filled with your last picks (the first time: Sentinel, Assassin, Pyromancer, Priest). Begin needs every box filled.
- In combat all heroes take part. Heroes are placed closer together and drawn a bit smaller so 8 fit on screen.
- The rewards screen shows a row per hero in two columns (rows drawn smaller, cards less so; `[Layout] RewardCardScale`), so every hero picks a card or dust and gets XP and
  gold. With 8 heroes you get twice the gold per fight (gold is paid per hero, as in vanilla).
- Events: every hero rolls (one row of small portraits, centred on the picked reply) and every hero gets the event's effects
  (XP, cards, HP, gold, items...). Group rolls add one card per hero, so with 8 heroes the target is doubled
  (`[Balance] ScaleGroupRolls`): "Group [5 or lower]" shows and works as "10 or lower" against the total of the cards shown.
  The odds, the result and critical results use the same target. Event gold is paid per hero, as in vanilla.
- End of run (resign, death or victory): a panel with rank progress for every hero, in two columns. Every hero gets its rank
  progress and the gold/dust reward is the vanilla single-player amount. The Tome of Knowledge run history records the
  first 4 heroes (its format has room for 4).
- On the rewards screen the deck view shows the deck before this screen's picks (same as vanilla): picked cards are added when
  the screen closes.
- Loot (treasure nodes): heroes 5 to 8 get a second row of panels under heroes 1 to 4 and pick in turn after them. The panels
  and the item cards are drawn smaller to fit. The number of
  items is the vanilla one, so once the items are gone the remaining heroes take the gold (paid per hero, as in vanilla).
- Map, town and event screens: the column of hero portraits on the left has a portrait for every hero (drawn smaller so 8 fit),
  so you can open the character window (deck, items, level, perks) for heroes 5 to 8. On the map, the tracked-requirements list
  under the portraits moves down to make room. In the character window the Exit button and the deck's energy cost graph move
  to the right side so they do not cover portraits 6 to 8. In town buildings and event shops the Exit button moves to the bottom
  left under the portraits (as do the Hide buttons of events and of the map's corruption screen), and the energy graph moves to the right (in the forge, smaller,
  at the top right; hidden in the saved-decks panel). Town shops and crafting work for heroes 5 to 8 (corruption fixed in 0.2.15).
- Map corruption rewards (gold and dust, random card upgrade) go to every hero (0.2.16). Gold and dust are paid per hero,
  as in vanilla.
- The combat stats screen shows every hero, two heroes between each pair of lines (portraits and numbers smaller), and
  its totals count every hero. The Tome of Knowledge's past runs keep their 4 heroes.
- Card minigames: every hero's portrait shows on the picked card; in the pairs memory game every hero takes turns (same
  number of flips as vanilla, so each hero flips less often).
- **No Steam leaderboards.** Scores from runs played with this mod installed are not posted to the Steam leaderboards (normal, Obelisk, Singularity and weekly), so modded runs do not mix with vanilla ones. The log says "Steam leaderboard score not posted" when a run ends.
- If fewer than HeroCount heroes are unlocked, the screen shows fewer boxes and the run is topped up from `[Spike] ExtraHeroes`.

## Requirements

- BepInEx 5.4 (win_x64 on Windows, linux_x64 on Linux).
- The [Enemy Scaling](https://github.com/cojoco/ato-enemy-scaling) mod (`EnemyScaling.dll`) must be installed too; More Heroes will not load without it.
  With twice the heroes you will want stronger enemies.

## Limits

- Single player. In multiplayer the mod does nothing unless the [More Players](https://github.com/cojoco/ato-more-players) mod is installed on every player's game
  (co-op with more than 4 heroes, rooms for up to 8 players; experimental).
- Adventure mode only, not the tutorial, Obelisk Challenge, weekly or Singularity.
- **Saves:** a run with more than 4 heroes needs this mod to load. Without it the game breaks at the first fight. Back up your
  save folder before trying it.

## Config

`BepInEx/config/com.cojoco.moreheroes.cfg` (created on first launch):

| Section | Key | Default | Meaning |
|---|---|---|---|
| General | Enabled | true | Add extra heroes to new runs. Runs that already have more than 4 heroes keep working when this is off. |
| General | HeroCount | 8 | Total heroes in a new run, 4 to 8 (= boxes on the hero selection screen). |
| Spike | ExtraHeroes | sentinel,assassin,pyromancer,priest | First-time picks for boxes 5 to 8, and fallback heroes if a run starts with fewer than HeroCount picked. |
| Selection | LastExtraHeroes | (empty) | Filled in automatically with your last picks for boxes 5 to 8. |
| Layout | HeroSpacing | 0 | Distance between heroes in combat (vanilla 1.9). 0 = automatic. |
| Layout | HeroScale | 0 | Hero size in combat (1 = vanilla). 0 = automatic. |
| Layout | HeroStagger | 0.9 | Vertical offset between neighbouring heroes in combat. |
| Layout | RewardColumns | 2 | Rewards screen: columns of hero rows. |
| Layout | RewardRowScale | 0 | Rewards screen: row size (1 = vanilla). 0 = automatic (about 0.6 with 2 columns). |
| Layout | RewardRowSpacing | 0 | Rewards screen: distance between rows (vanilla 2.2). 0 = automatic. |
| Layout | RewardColumnSpacing | 0 | Rewards screen: distance between columns (screen is about 19.2 wide). 0 = automatic. |
| Layout | RewardOffsetY | 0 | Rewards screen: move all rows up or down. |
| Layout | SelectionRowOffsetX | 0 | Hero selection: sideways shift of the second row of boxes. |
| Layout | SelectionRowOffsetY | 2.1 | Hero selection: distance from the first row of boxes down to the second. |
| Layout | EventRollColumns | 8 | Events: hero portraits per row (8 = one row, 4 = two rows). |
| Layout | EventRollScale | 0 | Events: size of the hero portraits and roll cards (1 = vanilla). 0 = automatic (about 0.30 in one row, 0.55 in two). |
| Layout | EventRollColumnSpacing | 0 | Events, one row: distance between hero columns. 0 = automatic (0.9). |
| Layout | EventRollCenterX | 0 | Events, one row: centre of the row. 0 = automatic (centred on the picked reply). |
| Layout | EventRollRowSpacing | 1.6 | Events, two rows: distance between the row of heroes 1-4 and the row of heroes 5-8. |
| Layout | EventRollOffsetY | 0.2 | Events: move the hero portraits up (positive) or down. |
| Layout | LootRows | 2 | Loot: rows of hero panels (2 = heroes 5-8 under 1-4, 1 = one row across the screen). |
| Layout | LootPanelScale | 0 | Loot: size of the hero panels (1 = vanilla). 0 = automatic (0.75 in two rows). |
| Layout | LootCardScale | 0 | Loot: size of the item cards (1 = vanilla). 0 = automatic (fills the space below the panels, about 0.84). |
| Layout | LootRowSpacing | 0 | Loot, 2 rows: distance between the rows. 0 = automatic. |
| Layout | LootOffsetY | 0 | Loot: move both rows of hero panels up or down. |
| Layout | FinishRunScale | 0 | End of run: size of the hero panels (1 = vanilla). 0 = automatic (0.8 with 8 heroes). |
| Layout | SidePortraitScale | 0 | Map/town/events: size of the hero portraits on the left (1 = vanilla). 0 = automatic (0.8). |
| Layout | SidePortraitSpacing | 0 | Map/town/events: distance between those portraits (world units). 0 = automatic. |
| Balance | ScaleGroupRolls | true | Events: group roll target x heroes / 4 (8 heroes: doubled). |
| Logging | ShowGameErrors | true | Log the game's own errors (to Player.log, see Problems). |

Hero ids: mercenary, sentinel, berserker, warden, cleric, priest, voodoowitch, prophet, elementalist, pyromancer, warlock,
loremaster, ranger, assassin, archer, minstrel, bandit, fallen, paladin, queen, engineer, valkyrie, alchemist, deathknight,
bloodmage, exile.

## Problems

Send `Player.log` (Linux: `~/.config/unity3d/Dreamsite Games/AcrossTheObelisk/Player.log`, Windows:
`%USERPROFILE%\AppData\LocalLow\Dreamsite Games\AcrossTheObelisk\Player.log`; the previous session is `Player-prev.log`). It has
both the game's own errors and the mod's lines; `BepInEx/LogOutput.log` only has the mod's lines. The mod writes a summary of its patches at startup, the box layout when the hero selection screen
opens, the picked heroes when a run begins, the row layout on the rewards and loot screens, the side portrait layout in each scene, the hero line-up at the start of every combat
and the roll setup and layout of every event.
