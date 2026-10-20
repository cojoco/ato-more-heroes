# MoreHeroes handoff (2026-10-20, after 0.2.24)

For the next agent working on this mod. Read this first, then `NOTES.md` (full history: causes, offsets, what was verified
offline vs guessed) and the sources. Multiplayer is a separate mod, `../MorePlayers` (section "Multiplayer").

## What this is

A BepInEx 5 mod for Across the Obelisk (Unity 2022.3.62f2, Mono, .NET target `netstandard2.1`) that puts up to 8 heroes in a
single-player Adventure run. It depends on the user's `EnemyScaling` mod (stronger enemies; `../EnemyScaling`). The user also
runs their own `DeckLogger` and `EncounterPicker` (`../dist` holds release zips). The user is learning BepInEx, plays on Linux,
and has friends on Windows. They test in game and send screenshots and `Player.log`; you cannot launch the game.

## State at 0.2.23: single player with 8 heroes (all confirmed in game unless marked)

- Combat, hero selection, rewards, events (group rolls scaled), loot, end-of-run screen, save slots (0.2.0-0.2.9). The user
  said not to spend more time on the event roll, end-of-run or loot layouts.
- Side portrait column with 8 portraits in every scene (`SidePortraits.cs`, 0.2.10-0.2.14), and everything that sat under it
  moved: character window Exit/energy graph, town building and event-shop Exit, event and corruption-screen Hide buttons, energy
  graphs (`SideClearance.cs`). User-approved layouts, don't redo them.
- Town for heroes 5..8: cards, items, pets, card/item corruption (0.2.15).
- Map corruption rewards (0.2.16): gold/dust for every hero, altar free upgrade. `randomcardupgrade` (each hero gets a random
  upgrade, then a list of upgraded cards) is shifted but **not yet seen in game**.
- Rewards screen (0.2.17-0.2.18): bigger cards, closer together, shards option after the last card; 3 and 4 cards per row.
- Combat stats / damage meter (0.2.19, `DamageMeterUi.cs`): 8 rows, two per painted band; totals and best-hero highlight count
  every hero; the Tome of Knowledge's past runs keep the vanilla 4-row layout.
- Card minigames (`MinigamesUi.cs`): shuffle game shows all 8 portraits on the picked card (0.2.20-0.2.21; event version 2 x 4
  grid, town version rows of 4, both confirmed; the reward always reached all heroes). Pairs memory game (0.2.22-0.2.23): every
  hero takes turns (16 flips as vanilla, 2 per hero with 8), centred 8-portrait turn strip, pairs go to the right heroes
  after reshuffles.
- Startup summary in `Player.log`: "Transpilers: 53/53 applied", no FAILED, no exceptions in the last tests.

### Not done / not tested in single player
- Town minigames other than the shuffle game: betting (Velkarath), card sequence memory (Aquarfall, Faeborg), spot the
  difference (Ulminin), pairs (Sahti town version; the 0.2.22 turn changes should apply). Not tested with 8 heroes.
- Blackjack / High-Low: unreachable in this game build (NOTES "Conflicts, Blackjack..."). Conflict screen: multiplayer only.
- Controller navigation: literal `j < 4` loops over `sideCharacters.charArray` build the controller lists, so heroes 5..8's
  portraits cannot be reached with a controller, and `CharacterWindowUI.ControllerNextCharacter` wraps `num > 3` to hero 1.
  Only worth doing if the user or friends play with a controller (ask first). Sites: CharacterWindowUI `ControllerMovement`,
  EventManager, CardCraftManager (5 sites), ConflictManager, CorruptionManager, CardPlayerManager, CardPlayerPairsManager
  `ControllerMovement`. The shift must follow `charArray.Length` (add a `SidePortraits.Shift`), not the team size.
- Order-dependent problems in town are not ruled out (the user tried each action once per hero).

## Multiplayer: separate mod `../MorePlayers` (started 2026-10-20)

Co-op work lives in `../MorePlayers` (read its HANDOFF.md and NOTES.md). The user chose: up to 8 players, heroes split as
in vanilla (the host gives boxes to players, a player may own several heroes). MoreHeroes only has the hook (0.2.24,
`MultiplayerApi`): without MorePlayers every co-op game stays at 4 heroes and MoreHeroes does nothing there. Keep it that way:
co-op code goes in MorePlayers, MoreHeroes gets at most small public hooks.

## Conventions (follow them)

- `HeroSlots.Count` is the single source of truth: team size clamped 4..8 in single player; in multiplayer 4 unless
  MorePlayers says the room has more heroes (`MultiplayerApi`).
  At 4 heroes everything must behave exactly like vanilla (no-op).
- Transpilers (`IlPatches.cs`) never replace a constant. They keep `ldc.i4 K` and insert `call Shift(int)` after it. Each
  spec lists the exact count of that constant in the method, which occurrences to shift, and the shape of the next
  instruction (`Shape.Branch`, `NewArr`, `Sub`, `Elem`, `Store`, `Field`, `Rem`, `Any`); on any mismatch the method stays
  vanilla and an error is logged. A spec has one shift method (`M(name, resolve, shiftWith, ...)`, default `HeroSlots.Shift`);
  a site can override it (`S(MethodInfo, constant, count, ...)`, 0.2.22) when one method mixes counts (pairs game: turn slots
  vs. team loops). Non-constant edits use `E(...)` with a custom edit function (e.g. `RewardsUi.EditCardPitch`).
  UI loops use a shift that follows what actually exists (`RewardsUi.Shift`, `HeroSelectUi.Shift`, `EventsUi.ShiftRollSlots`,
  `DamageMeterUi.Shift`), so a failed clone keeps 4.
- Screens built for 4: clone the scene objects at runtime in a Start/Awake prefix or postfix, grow the arrays (build
  everything first, then assign), lay out, and log the layout plus a delayed "layout check" with renderer bounds
  (`RewardsUi.VisibleBounds`; uGUI screens: `RectTransform.GetWorldCorners`). Put layout numbers in `[Layout]` config keys,
  0 = automatic, and re-read config when the screen opens (`Plugin.ReloadConfig`).
- Some objects have their scale driven by an Animator (card portraits: setting the slot's own scale did nothing, 0.2.20);
  scale the parent instead.
- Small crash guards go in `Guards.cs`. Diagnostics go in `Diagnostics.cs`.
- Every patch class must be added to the list in `Plugin.Awake` so it appears in the startup summary.
- The profile `player.ato` is shared with vanilla: never write anything vanilla cannot read (`LastUsedTeam` stays 4 entries; the
  run history keeps 4 heroes).
- Never copy game source into the project. The decompile goes in the session scratchpad.
- Bump `Plugin.PluginVersion`, add a NOTES.md section (cause, fix, verified offline vs guessed), and update README.md if users
  can see the change. The build must be clean (0 warnings, 0 errors).
- Match the code style: comment density, naming, try/catch around every patch body, log lines a person can read.
- Measure with renderer bounds, never with a single early reading: TextMeshPro meshes are not built on the frame a screen
  opens (0.2.13 forge graph). Re-measure, or wait until two readings agree.
- When the game shows something from an `async void` method or a coroutine, a Postfix can run before it is placed. The
  `SideClearance` pattern (a component on the runner object that checks a few times a second and only moves what is off target)
  worked; keep moves idempotent (compute from the current bounds, skip when already in place).
- Scenes are 16:9 at 104 px per world unit on the user's 2000x1125 screenshots: world x = px / 104.17 - 9.6, y = 5.4 - py / 104.17.
  Useful for reading positions off a screenshot; say in NOTES when a number came from one.
- The csproj references `UnityEngine.Physics2DModule` (BoxCollider2D) and `UnityEngine.UI`. Add other Unity modules the same
  way when needed (Photon: the game ships `PhotonUnityNetworking.dll` / `Photon3Unity3D.dll` in Managed; EnemyScaling
  references them).

## The HarmonyX bug (important)

BepInEx 5.4's HarmonyX 2.9 drops a `leave` that ends a try body or a catch handler when it re-emits a patched method, and
replaces it with a leave to the end of the exception block. If the original leave jumped further (a foreach inside if/else),
the patched method silently runs the wrong branch. This applies to every patch type, prefix-only too. `HarmonyFix.cs` fixes
it for every patched method (from any mod) by inserting nops, and must stay the last thing `Plugin.Awake` patches. If you
patch something later (lazily), run the fix on it too. If a patched game method misbehaves without an exception, suspect this
first. Details are in NOTES.md, 0.2.5, Tactical Thinking.

## Build, deploy, logs

- Build: `dotnet build MoreHeroes/MoreHeroes.csproj -c Release`. A post-build step copies the DLL to
  `~/.steam/steam/steamapps/common/Across the Obelisk/BepInEx/plugins/`. BepInEx does not hot-reload: the user must relaunch.
  Check the deployed DLL's version with `strings -e l .../plugins/MoreHeroes.dll | grep 0.2.`.
- Logs: the game's own errors and the mod's lines are in `~/.config/unity3d/Dreamsite Games/AcrossTheObelisk/Player.log`
  (previous session: `Player-prev.log`), read with `grep -a`. Copy them to the scratchpad early, because a relaunch rotates
  them. The game hides its own exceptions in release builds; `[Logging] ShowGameErrors` turns them back on. For multiplayer,
  the friends' logs on Windows are in `%USERPROFILE%\AppData\LocalLow\Dreamsite Games\AcrossTheObelisk\Player.log`.
- Config: `BepInEx/config/com.cojoco.moreheroes.cfg`.

## Offline tooling (recreate each session; /tmp is wiped on reboot)

- Decompile: `dotnet tool install ilspycmd --tool-path <scratch>/tools`, then
  `DOTNET_ROLL_FORWARD=Major <scratch>/tools/ilspycmd -p -o <scratch>/dec ".../AcrossTheObelisk_Data/Managed/Assembly-CSharp.dll"`.
  IL dump for counting constants: the same command with `-il` instead of `-p -o <dir>`, redirected to `<scratch>/il.txt`.
  `python3 tools/ilcount.py <il.txt> <first line> <last line>` prints the 3/4/5/8 constants per method in that line range
  (find a class with `grep -n '^.class .* LootManager$' il.txt`; the class label it prints can be off, the method names are right).
  Then `grep -A1 'ldc.i4.4$'` in the same range shows what follows each one (branch, rem, stfld state, GetChild...).
  Coroutine bodies are in `<Name>d__N::MoveNext`; a `ldc.i4.N` followed by `stfld <>1__state` is a coroutine state, never shift it.
- Transpiler check: `tools/ilcheck/` (in the project). Build it with `-o <scratch>/ilcheck`, then run it with
  `MANAGED=".../AcrossTheObelisk_Data/Managed" MOD=MoreHeroes/bin/Release/netstandard2.1/MoreHeroes.dll dotnet <scratch>/ilcheck/ilcheck.dll`.
  It runs the mod's own transpiler on every spec against the real IL, prints ok/SKIPPED, and simulates the HarmonyX leave rule.
  `SHOW=<spec name>` prints the inserted calls in context; `DUMP=<spec name> OUT=<path>` writes the IL before and after. It
  cannot run Unity methods, so Prefix/Postfix bindings are only checked by the in-game startup summary.
- Scenes and assets: `python3 -m venv <scratch>/upy && <scratch>/upy/bin/pip install UnityPy TypeTreeGeneratorAPI`, run scripts
  with `<scratch>/upy/bin/python -I`. Scripts in `tools/unitypy/` (`<Data>` = `.../AcrossTheObelisk_Data`):
  - `scenes.py <Data>`: scene numbers and names (`levelN` = scene N). Known: 1 DamageMeter (DontDestroyOnLoad), 4 HeroSelection,
    9 Town, 10 Map, 11 Rewards, 12 FinishRun, 15 Loot, 17 CardPlayer, 19 CardPlayerPairs, 22 RewardsMultiple, 24-27 town
    minigames (Betting, SpotTheDifference, Blackjack, HighLow).
  - `tree.py <levelN path> <depth> [rootName]`: transform tree with local positions and scales.
  - `rtree.py <levelN path> <depth> <rootName>`: the same with RectTransform fields and layout components (uGUI screens).
  - `mbraw.py <Data> <levelN> <names>`: script class names of a GameObject's MonoBehaviours from raw bytes (scene
    MonoBehaviours often have no typetree), with their field bytes in hex (decoded a GridLayoutGroup this way).
  - `sprite_of.py <Data> <levelN> <GameObject> <out dir>`: saves the sprites a GameObject's UI components use, as PNG.
  - `findgo.py <Data> <GameObject name>`: lists the `levelN` scene files that contain a GameObject of that name.
  - `cards3.py <Data> <out.json>`, `show.py <cards.json> <ids...>`, `raw.py <Data> <card>`: card data.
  - `minigame_events.py <Data> <out.json>`, `event_nodes.py <Data> <eventId,...>`, `one_event.py <Data> <eventId>`: events
    (which replies start a minigame, which map nodes/zones roll an event, one event's replies). Minutes each; run in background.
- `[Layout]` defaults: BepInEx keeps values already in the user's .cfg, so a changed default does not reach the user's install
  (0.2.8 `LootRows`). If you change a default the user has already run, edit their cfg (layout keys are re-read when the screen
  opens) or tell them.
- Bash: use absolute paths; write longer Python as a script file in the scratchpad and run it (inline heredocs and `cd ...; cmd`
  compounds sometimes got no verdict from the auto-mode permission classifier). Inserting text into C# with a script: a
  semicolon inside a string literal fooled a naive "find the end of the statement" search once (0.2.22); check the build.

## Working with the user

- Give the user short in-game test checklists and ask for screenshots plus `Player.log`. Read the log yourself rather than
  asking what it says.
- Report what is verified offline and what is guessed. Don't claim a fix works until the user has confirmed it in game.
- When the user asks where something appears in the game (an event, a minigame), answer from the game data with the
  `tools/unitypy` event scripts rather than from memory.
- Memory notes for this project are in `~/.claude/projects/-home-connor-projects-across-the-obelisk-mods/memory/`
  (`project-8-hero-mod-research.md`, `harmonyx-leave-bug.md`, `ato-decompile-and-game-api.md`, `ato-mod-build-setup.md`).
