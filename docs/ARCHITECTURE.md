# LivingNPC – Architecture

LivingNPC is a game built on **places** (a picture with buttons at x, y coordinates) and a **simulated world**: every
character follows its own routine, quests and messages react to time, place and state, and **all content comes from
external files**. The simulation runs on Unity **DOTS (Entities 1.4)**.

This page is for developers who want to change or extend the engine. To write content, see the
[JSON reference](JSON_REFERENCE.md) and the [Studio language](STUDIO_COMMANDS.md).

## Principles

1. **Content is data, not code.** Places, characters, routines, quests, messages and rules are JSON and pictures in
   folders. The game has no hard-coded content.
2. **Three models, one direction of conversion.**

   ```
   Files (JSON/PNG)  ──load──▶  Authoring model (C# classes, strings)  ──compile──▶  Runtime model (ECS + Blob, int ids)
   ContentLoader                 ContentDatabase / *Def                  ContentCompiler   WorldBuilder
   ```
   - *Loader*: finds the packs, merges them by priority, never throws (problems go to the `ValidationReport`).
   - *Compiler*: checks every reference (unknown place, character, stat, quest…), compiles conditions and effects into
     flat arrays (`CondNode`, `FxNode`), builds the navigation graph (all-pairs shortest paths) and the `BlobAsset`s.
   - *WorldBuilder*: creates the entities. No subscenes or baking: the world is rebuilt from the files at runtime, which
     is what makes mods, hot reload and AI-written content possible.
3. **Simulation separate from the UI.** The UI (uGUI in `GameView`, IMGUI menus) reads the ECS state and only writes
   `GameCommand`s. The view can be replaced (UI Toolkit, 3D) or the world can run headless (tests, an AI playing)
   without touching the logic.
4. **Event-driven time, not real time.** Time only moves when something asks for it (travelling, waiting, sleeping,
   the `advanceTime` effect). `ClockSystem` simulates **one tick (5 minutes by default) per frame**: a 15-minute trip
   is 3 ticks, and quests and messages can fire at the exact minute. An open dialogue freezes time.

## Content packs

```
StreamingAssets/Content/core/           engine defaults (read-only)
<repo>/worlds/<world>/<pack>/           a world = a folder of packs; LivingNPC ▸ Choose World, or env LIVINGNPC_WORLD
<persistentDataPath>/Mods/<pack>/       user mods (writable)
unity/LivingNPC/Mods/<pack>/            editor only, handy while developing
$LIVINGNPC_MODS/<pack>/                 optional extra folder
```

The pack format is described in the [JSON reference](JSON_REFERENCE.md#worlds-and-packs).

**Where the worlds are in a build** (`ContentLoader.WorldsDir`): `LIVINGNPC_WORLDS` if set, otherwise a `World` or
`Worlds` folder next to the executable. The folder can be a single world (`World/base/…`: the game ships with its
world) or hold several (`Worlds/<world>/base/…`). The menu shows the chosen world with its title (`name` in
`project.json`); "Load world" appears when there is more than one.

**Title screen** (`title.json`): the image fills the screen (cropped, never stretched), the music loops while the
menu is open, and the title is written only if `drawTitle` is true. A missing file is written at the bottom left of the
menu and in the log. `buttons` gives two pictures (normal; hover / pressed / selected) to every button made by
`UiKit.Button` (`SkinnedButton` component: sprite swap, greyed out when it cannot be pressed). The picture is scaled to
the button's height and stretched only in width: `border` is the number of pixels at the left and right ends that keep
their shape (3-slice). The IMGUI menu draws them the same way (`GameBootstrap.DrawSliced`).

**Merge rules** (packs ordered by `priority`, then dependencies): an id defined again is *replaced*.
`"$op": "merge"` deep-merges into the previous definition (lists are replaced); `"$op": "delete"` removes it. A pack
with only `project.json` + `nodes/` is valid: it is what the map editor writes.

**In game:** `F5` reloads everything from disk **keeping the game in progress** (it is saved in memory and applied
again; an open dialogue or a trip restarts). `F6` saves and `F9` loads (slot `quick`); every new day writes the `auto`
slot. `Esc` opens the game menu (`SaveMenu`) and pauses the simulation: Save / Load / Delete on 9 pages of 6 slots
(`1-1` … `9-6`) plus page A (`auto`, `quick`, load only), Settings, Main menu, Quit. On the title screen, "Load" and
"Settings" open the same screen. Settings (`GameSettings`: master, music, ambient and effects volume, fullscreen /
window, position of the panels) are in `<persistentDataPath>/settings.json`, outside the saves. The Present, Journal
and Backpack panels (`DraggablePanel`) can be dragged by their title bar (a click folds them).

**Debug (F12):** `DebugPanel` shows where every npc is and why (the routine block that applies, a forced move), the
quests and events with their conditions evaluated now branch by branch (`RuleRuntime.EvalTree`), each test with the
value it reads (`flag door ≥ 1 (now 0)`), the events that ran, the clock, flags and links. Tab in the console completes
command names and the ids the argument expects. Its buttons and the
`DebugConsole` below it run commands written as in the Studio (`modStat(energy, -10, target=all)`, `run(ev_id)`,
`where(lia)`, `help`, and `resetQuest` / `resetEvent` to play a quest or an event again): each line is compiled by `CompiledContent.TryCompileConsoleEffect` with the content's checks
(only the effects made of ids and numbers: `ContentCompiler.ConsoleEffects`) and run by `RuleRuntime.ApplyOne`, so
rules react as to a quest's effect. The console's argument table must match `studio/lib/specs.js`
(`studio/tests/console.mjs` checks it). A game changed this way is marked: `WorldState.DebugUsed`, saved in the
`debug` section and in `meta.debugUsed` (the save list shows "debug"). The panel is on in the Editor and development
builds, and in a release build when Settings ▸ Debug panel is On.

The interface written in code (menus, HUD, notices) is in English; the language of the content is chosen by
`rules.json`. `AudioPlayer` applies the volumes: music = the `Ids.MusicKey()` channel and the title music, ambient =
the other loops, effects = one-shot sounds.

The content report is written to `<persistentDataPath>/content_report.txt` (and **LivingNPC ▸ Validate Content** in
the editor). Every line gives the file, the id and the problem, so it can be handed to an AI that fixes the file.

> Platforms: the loader reads real files. On Windows, macOS and Linux `StreamingAssets` is a real folder; Android and
> WebGL would need it copied to `persistentDataPath` on first launch (not done yet).

## Simulation (ECS)

| What | Where | Notes |
|---|---|---|
| Global state | the "world" entity: `ClockState`, `WorldState`, buffers `FlagValue`, `QuestState`, `EventState`, `GameMessage`, `GameCommand`, `DialogChoice`, `EntityRef` | one entity = easy saving |
| Player | `PlayerState` + `StatValue[]` | `Node == 0` while travelling |
| Characters | `NpcTag`, `NpcLocation`, `NpcInfo` (age, sex), `NpcAuto`, `ScheduleBlock[]`, `NpcPlace[]`, `StatValue[]`, `RelationValue[]`, `NpcMemory[]` | one entity per character |
| Inventory | `DynamicBuffer<InventorySlot>` `{Item, Count}` on the player and on every character; definitions in `RuleBlob.Items` | API: `RuleRuntime.GetItemCount / AddItem` |
| Static data | `NavGraphBlob`, `RuleBlob` (BlobAsset) | read by Burst jobs |

System order per frame: `CommandSystem → ClockSystem → NpcSystem (parallel IJobEntity, Burst) → RuleSystem`.

- **Characters** (`NpcTickJob`): *plan* (the routine block with the highest priority now; they leave early to arrive
  on time, using the graph distances) → *move* (shortest path hop by hop, precomputed `NextHop`; link times do not
  depend on the tick length) → *stats* (drift towards the baseline + effects of the current activity). With no active
  block a character goes home. Routines that cross midnight are split when compiled.
- **Where are the characters when you move?** The world is global: when time passes everyone moves, also off screen.
  "Frozen" maps would only need `NpcSystem` to run for the active places.
- **Routines can change at runtime:** named places (`home`, `work`…) live in the `NpcPlace` buffer and a block written
  `$place` resolves them on every read (`ScheduleBlock.Place`); blocks carry a `Tag` (their `id`) so they can be
  replaced or removed; blocks added by effects are compiled into `RuleBlob.RoutineBlocks / RoutineSets`.
- **Conditions** (`RuleRuntime.Eval`): a flattened tree, evaluated iteratively (no recursion, Burst-friendly). Types:
  `all/any/not`, `always`, `time`, `day`, `date`, `after`, `elapsed`, `playerAt`, `npcAt`, `npcWithPlayer`, `stat`,
  `flag`, `quest`, `activity`, `hasItem`, `relation`, `goal`, `age`, `sex`, `chance`, `doing`, `did`, `busy`.
- **Effects** (`RuleRuntime.Apply`): `message`, `setFlag/addFlag`, `startQuest/advanceQuest/completeQuest/failQuest`,
  `modStat/setStat`, `giveItem/takeItem`, `moveNpc`, `setPlace/addRoutine/removeRoutine/setRoutine`,
  `advanceTime/advanceTimeTo`, `teleportPlayer`, `showLink/hideLink/openLink/closeLink`, `showImage/hideImage`,
  `playSound/startMusic/stopMusic/startAmbient/stopAmbient`, `tell`, `modRelation/setRelation`, `setGoal`,
  `setAutoQuests/startAuto/stopAuto`.
- **Persistent pictures and sounds** are flags, like links: `Ids.ImageKey(name)` holds *image index + 1* while the
  picture is shown (0 = hidden) and `GameView.RefreshOverlays` draws them; music and ambient loops use
  `Ids.MusicKey()` / `Ids.AmbientKey(name)` = *index in `CompiledContent.Sounds` + 1*, passed by
  `GameView.RefreshSounds` to `AudioPlayer.SetLoop`. One-shot sounds (`playSound`, `sound` steps) are events, not
  state: the simulation adds a `SoundCue` to the world buffer and `GameView.PlayCues` plays and clears them every frame.
- **Quests** are state machines: `autoStart`, then per stage `complete` / `fail` / `onComplete` / `onFail`, `next` to
  branch. A stage without `complete` only advances with `advanceQuest`.
- **Messages** (events): triggers `enterNode`, `tick`, `talk`, `wake`; `when`, `once`, `cooldownMinutes`, `priority`
  and a `script` of steps (lines, pictures, sounds, effects, `if`, `choices`) compiled into `ScriptStep`s.
- **Texts** (`TextTable`): a string or `{ "en": "...", "it": "..." }`; ECS only keeps the integer id. Placeholders
  are replaced by the view (`CompiledContent.Format`): `{npc}` (with `.first`, `.last`, `.full`, `.age` and the forms
  `{npc:he|she|they}`), and `{#k}` = value k of the automatic quest that wrote the line (`GameMessage.Bind`; the
  compiler turns `{self}` and the slot names into `{#0}`, `{#1}`…).
- **Automatic quests** (`AutoQuests.cs`, `ContentCompiler.Auto.cs`): templates (`TemplateRule`) an npc picks by
  itself. Every `AutoCheckMinutes` (`RuleRuntime.CheckAutoQuests`, on a tick) each free npc with `NpcAuto.Enabled`, past
  its pause, scores the templates: `when` with self bound, slots filled at random (`FillSlots`: candidates that pass
  the slot's condition with the candidate bound to it), `WeightTerm`s; one is drawn weighted by score (at most
  `AutoMaxActive` run at once). The instance lives in `NpcAuto` (template, stage, `Bind` = [self, slots...], `Roll`
  for `chance`, `Seed`); `EvaluateAuto` runs its stages like quests'. Conditions and effects of a template store
  `BindRef` values (`int.MinValue + k`, a range `Ids.Hash` never returns) in place of ids; `RuleRuntime.R()` replaces
  them with the instance's values (`RuleRuntime.Bind`, set by `Enter` / `RestoreCtx`). Texts are groups of variants
  (`TextGroupRule` / `TextVariantRule`), said by `tell` into the log (`MsgKind.Npc`) or when the player talks to a
  busy npc (`TalkAuto`). When it ends the npc remembers it (`NpcMemory`, last 16) and rests (`NextAt`). The giver of
  an active quest with `busyGiver` (`QuestRule.BusyNpc`, `RuleRuntime.BusyQuest`) picks none and is no free slot;
  the `busy` condition (`RuleRuntime.IsBusy`) is the same test free slots use.

## Saves (`Assets/Game/Runtime/Simulation/Save`)

JSON files in `<persistentDataPath>/saves/<world>/<slot>.json` (plus `.bak` of the previous save and `<slot>.png`, the
screenshot at the moment of saving): a summary (`meta`: game time, place, packs) and **one section per kind of state**:
`clock`, `flags`, `links`, `images`, `sounds`, `quests`, `events`, `messages`, `player`, `npcs` (with the relations,
the memory and the running automatic quest of each npc: template and stage by id, slots by id). Rules (details in
`ISaveSection`):

- everything is stored **by text id** (`"q_lost_notebook"`, stage `"return"`), never hashes or indices: renamed or
  removed content does not break a save;
- only **state** is saved, not content (definitions, base routines and prices stay in the JSON);
- `Apply` runs on a world freshly built from the content: what the file lacks keeps its content value; an id that no
  longer exists is skipped with a warning (`SaveContext.Warnings`), never an exception; an unknown section is kept and
  written back on the next save;
- the game only saves when it is still (`SaveGame.CanSave`: no dialogue, trip or wait in progress);
- old save layouts are **not** kept loadable: when the layout of a section changes, raise `SaveGame.FormatVersion`
  (and update the sample save in `ContentMenu.SmokeTest`). Saves of another version are refused with a clear message.

A new system with its own state (equipment, market, skills…) = a new `ISaveSection` and one line in
`SaveGame.Sections`. Stats, flags and inventories are already saved generically: anything that can be modelled as a
stat or an item needs no code.

## How to extend it

| I want… | I change |
|---|---|
| a new place / character / quest / message | only JSON files (no code) |
| a new stat / activity | `rules.json` |
| a new kind of condition or effect | the enum in `Blobs.cs`, `RuleRuntime` (`Eval` / `Apply`), `ContentCompiler`; then add it to `studio/lib/specs.js` so Studio knows it |
| a new system (economy, weather…) | a new `ISystem` + components on the world / character entities; expose conditions and effects as above |
| state to save | an `ISaveSection` class in `Simulation/Save/` and a line in `SaveGame.Sections` |
| another UI | replace `GameView`, keep the same `GameCommand`s |
| tests / an AI playtester | `LivingNPC ▸ SmokeTest` shows how to drive the world headless |

## Code map (`Assets/Game`)

```
Runtime/Core          Ids (string → int), GameTime, TextTable
Runtime/Content       ContentDefs (DTOs), ContentLoader (packs + merge), ContentCompiler, ValidationReport
Runtime/Simulation    Components, Blobs, RuleRuntime (Eval / Apply), Systems, WorldBuilder, Save/
Runtime/Presentation  GameBootstrap (start-up, menus, F5/F6/F9/F12), GameView (uGUI), SaveMenu, UiKit, AudioPlayer,
                      DebugPanel + DebugConsole (F12)
Editor                ContentMenu (Validate Content, Choose World, SmokeTest, SoundSmokeTest)
```

## Tests

- `unity/LivingNPC/SmokeFixtures/README.md` – the headless game tests (Unity batch mode).
- `studio`: `npm test` – script ⇄ JSON conversion of every world and the checks of characters, routines, items, rules.
- `map-editor`: `npm test` – end-to-end test of the map editor server.
