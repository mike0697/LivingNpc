# LivingNPC – JSON reference

Everything in a LivingNPC game is data: places, characters, routines, items, quests, messages and rules are plain
JSON files next to the pictures and sounds they use. The game reads them when it starts (and again with **F5**), checks
them and builds the world. You can write them by hand, with the editors, or with the help of an AI assistant.

> **Tip for AI assistants:** this page is written so it can be given to a language model as its instructions. The
> [rules for authors](#rules-for-authors-and-ai-assistants) at the end are the short version.

> **Alpha:** the format may still change. Breaking changes will be listed in the release notes.

## Contents

- [Worlds and packs](#worlds-and-packs)
- [Common formats: ids, texts, times, days](#common-formats)
- [pack.json](#packjson) · [project.json](#projectjson) · [title.json](#titlejson) · [rules.json](#rulesjson)
- [Places: nodes/&lt;id&gt;/node.json](#places-nodesidnodejson)
- [Characters: npcs/&lt;id&gt;.json](#characters-npcsidjson) · [Shared routines: schedules/&lt;id&gt;.json](#shared-routines-schedulesidjson)
- [Items: items/&lt;id&gt;.json](#items-itemsidjson)
- [Quests: quests/&lt;id&gt;.json](#quests-questsidjson) · [Messages: events/&lt;id&gt;.json](#messages-eventsidjson)
- [Conditions](#conditions) · [Effects](#effects) · [Script steps](#script-steps)
- [Patching another pack: $op](#patching-another-pack-op)
- [Checking your content](#checking-your-content)
- [Rules for authors and AI assistants](#rules-for-authors-and-ai-assistants)

## Worlds and packs

A **world** is a folder in `worlds/`. It contains one or more **packs**; the main one is called `base`:

```
worlds/
  aetherhall/
    base/
      pack.json            the pack: id, name, author, priority
      project.json         the world: game title, start place (written by the map editor)
      title.json           title screen and button pictures (optional)
      rules.json           clock, stats, activities, parts of the day, sleep, starting inventory
      nodes/<id>/node.json places (one folder per place, with its background pictures)
      npcs/<id>.json       characters
      schedules/<id>.json  routines shared by several characters
      items/<id>.json      items
      quests/<id>.json     quests
      events/<id>.json     messages (also in subfolders: events/<quest id>/<id>.json)
      images/              pictures used by characters, messages and title.json
      music/               music (wav, ogg, mp3)
      sfx/                 sound effects and ambient loops
      lore/*.md            free notes about the world (not read by the game; useful for writers and AI)
```

The game loads, in order:

1. `unity/LivingNPC/Assets/StreamingAssets/Content/core` – the engine defaults (do not edit);
2. the packs of the chosen world;
3. **mods**: packs in `<user data>/Mods/` (on Windows `%USERPROFILE%\AppData\LocalLow\LivingNPC\LivingNPC\Mods`),
   in `unity/LivingNPC/Mods/` (Unity editor only) and in the folder named by the `LIVINGNPC_MODS` environment variable.

Packs are merged by `priority` (higher wins). A file with an id that already exists **replaces** the old one, unless
it uses [`$op`](#patching-another-pack-op).

**Where the game looks for worlds:** in the Unity editor, `worlds/` at the root of the repository. In a built game, a
`World` or `Worlds` folder next to the executable. A `World` folder can be a single world (`World/base/...`): the game
then starts straight into it. The `LIVINGNPC_WORLDS` environment variable overrides both, and `LIVINGNPC_WORLD` picks
a world by folder name.

## Common formats

- **Ids** are `snake_case` strings: letters, digits, `_`, `-`, `.`. They must be unique within their kind. Every
  reference (a place in a link, a character in a quest, a stat in an effect…) must point to an id that exists.
- **Texts** shown to the player are either a string or an object with one entry per language:
  `"Hello"` or `{ "en": "Hello", "it": "Ciao" }`. The language is chosen in `rules.json` (default `en`).
  `{npc}` inside a message text is replaced with the name of the character the player is talking to.
- **Times** are `"HH:mm"` on a 24-hour clock; `"24:00"` is allowed as an end. A range whose `to` is earlier than its
  `from` crosses midnight (`"22:30"` → `"06:30"`).
- **Days:** `"mon" "tue" "wed" "thu" "fri" "sat" "sun"`, or `"weekdays"`, `"weekend"`, `"all"`, or a list:
  `["mon", "wed"]`. The game starts on day 0 (shown as "Day 1"), whose weekday is `rules.startDayOfWeek`.
- **Positions** of links and pictures are **percentages** of the map area, from the top-left corner:
  `x`, `y`, `width`, `height`. For a picture `x`, `y` are its top-left corner; for a link button they are its
  **centre** (as placed in the map editor). When `height` is omitted, a picture keeps its proportions.
- **Files** are paths relative to the folder they belong to (`images/`, `music/`, `sfx/`, or the place's folder),
  and can include subfolders: `"people/elena.png"`.
- Field names are not case-sensitive, unknown fields are ignored, and missing fields get their default.

## pack.json

```json
{ "id": "base", "name": "Aetherhall", "author": "Your name", "version": "1.0", "priority": 100,
  "description": "Example world.", "dependsOn": [], "enabled": true }
```

| Field | Default | Meaning |
|---|---|---|
| `id` | folder name | Unique pack id. |
| `name`, `author`, `version`, `description` | | Shown in the content report. |
| `priority` | `100` | Higher priority packs override lower ones. |
| `dependsOn` | `[]` | Packs that must load before this one. |
| `enabled` | `true` | `false` skips the pack. |

## project.json

Written by the **map editor**. The game reads `name` (the game title on the menu) and `startNode` (where the player
starts). `nodes[].graph` only stores the positions of the places in the editor's graph.

```json
{ "format": "livingnpc-map", "version": 1, "name": "Aetherhall", "startNode": "bed_1",
  "nodes": [ { "id": "bed_1", "graph": { "x": 674, "y": 1343 } } ] }
```

## title.json

The title screen and the look of the buttons. Optional; read from the `base` pack first.

```json
{ "image": "places/secret_passage.png", "drawTitle": true,
  "music": "magic-mystery.mp3", "musicOn": true, "volume": 1,
  "buttons":     { "normal": "buttons/normal.png", "hover": "buttons/hover.png", "border": 240, "textColor": "#4a2516" },
  "menuButtons": { "normal": "buttons/normal.png", "hover": "buttons/menu_hover.png", "border": 240, "textColor": "#4a2516" } }
```

| Field | Meaning |
|---|---|
| `image` | Background of the title screen (file in `images/`), cropped to fill the screen, never stretched. |
| `drawTitle` | Write the game title over the image (`false` when the title is already painted in the image). |
| `music`, `musicOn`, `volume` | Music of the menu (file in `music/`), on/off, volume 0–1. |
| `buttons` | Pictures of every button in the game: `normal`, `hover` (also pressed/selected), `textColor`. |
| `menuButtons` | The same for the main menu buttons (omitted = `buttons`). |
| `border` | Width in pixels of the left and right ends of the picture that are never stretched (3-slice). For a pill-shaped button: about half the picture's height. Crop transparent margins out of the picture first. |

## rules.json

```json
{ "tickMinutes": 5, "startMinute": 485, "startDayOfWeek": 0, "defaultLinkMinutes": 10, "language": "en",
  "startNode": "bed_1",
  "playerInventory": { "bread": 2, "gold_coin": 100 },
  "stats": [ { "id": "happiness", "name": { "en": "Happiness" }, "min": 0, "max": 100, "baseline": 60,
               "driftPerHour": 0.05, "constantPerHour": 0 } ],
  "activities": [ { "id": "sleep", "name": "Sleeping", "effects": { "energy": 12, "happiness": 0.5 } } ],
  "dayParts": [ { "id": "evening", "from": "19:00", "to": "23:00", "background": "night_light" } ],
  "sleep": { "from": "20:00", "wakeAt": "07:00",
             "effects": [ { "do": "setStat", "stat": "energy", "value": 100 } ] } }
```

| Field | Default | Meaning |
|---|---|---|
| `tickMinutes` | `5` | Game minutes simulated per step. Time only moves when the player travels, waits or sleeps, or an effect moves it. |
| `startMinute` | `480` | Start time, in minutes after midnight (`485` = 08:05). |
| `startDayOfWeek` | `0` | Weekday of the first day: 0 = Monday … 6 = Sunday. |
| `defaultLinkMinutes` | `10` | Travel time of a link without `minutes`. |
| `language` | `"en"` | Which entry of multi-language texts is shown. |
| `startNode` | | Start place, if `project.json` has none. |
| `playerInventory` | `{}` | Items the player starts with: `{ item id: count }`. |
| `stats` | | Numbers every character and the player have. `min`, `max`, `baseline` (default 50); `driftPerHour` = fraction per hour pulled back towards `baseline`; `constantPerHour` = fixed change per hour. |
| `activities` | | What characters do in their routine. `effects` = change per hour of each stat while doing it. |
| `dayParts` | | Which background a place shows at which time: `background` is a key of `node.backgrounds`. |
| `sleep` | | When the player can sleep: from `from` until `wakeAt` the next morning, in places tagged `bed`. Sleeping skips to `wakeAt` (the world keeps living, no dialogue interrupts it), then runs `effects` and the `wake` messages. |

## Places: nodes/&lt;id&gt;/node.json

A place is a picture with buttons (links) to other places. Written by the **map editor**.

```json
{ "id": "alley", "title": "Whisper Alley", "text": "A narrow street.", "tags": ["bed"],
  "backgrounds": { "day": "bg.png", "afternoon": "afternoon.png", "night": "night.png", "night_light": "night_light.png" },
  "links": [
    { "id": "alley_to_square", "target": "square_fountain", "label": "Square",
      "x": 72, "y": 68, "width": 17, "height": 6.5,
      "minutes": 10,
      "requires": { "type": "time", "from": "06:00", "to": "22:00" },
      "lockedText": "The gate is closed at night.",
      "visible": true, "open": true } ] }
```

| Field | Meaning |
|---|---|
| `title`, `text` | Name of the place and an optional description. |
| `backgrounds` | Pictures in the place's folder, by part of the day (see `rules.dayParts`); `day` is the fallback. |
| `tags` | `"bed"` = the player can sleep here. |
| `links[]` | Buttons to other places. Links go **one way**: add the opposite link to come back. |
| `links[].id` | Unique id, used by `showLink`, `hideLink`, `openLink`, `closeLink`. |
| `links[].minutes` | Travel time (default `rules.defaultLinkMinutes`). |
| `links[].requires` | A [condition](#conditions): when false the link is locked and `lockedText` is shown. |
| `links[].visible`, `open` | Initial state (default `true`). A hidden link is not drawn; a closed one is drawn but locked. |

## Characters: npcs/&lt;id&gt;.json

```json
{ "id": "lia", "name": "Lia", "description": "A student who loves books.", "tags": [],
  "places": { "home": "bedroom", "school": "guild", "hangout": "square_fountain" },
  "routine": [ "sleep_night", "school_timetable",
               { "id": "evening_walk", "days": "all", "from": "16:30", "to": "21:30", "at": "$hangout",
                 "activity": "relax", "priority": 6 } ],
  "stats": { "happiness": 65 },
  "inventory": { "notebook": 1 },
  "images": {
    "default":  { "file": "lia.png", "x": 40, "y": 30, "width": 20 },
    "activity": { "sleep": { "file": "lia_sleeping.png", "x": 30, "y": 55, "width": 30,
                             "nodes": { "inn_bedroom": { "x": 52, "y": 60 } } } },
    "talk":     { "default": { "file": "lia_talk.png", "x": 5, "y": 10, "width": 35 },
                  "happy":   { "file": "lia_happy.png", "x": 5, "y": 10, "width": 35 } } } }
```

| Field | Meaning |
|---|---|
| `name`, `description` | Shown in the game. |
| `places` | The character's named places. A routine block can say `"at": "$home"` instead of a place id; `setPlace` can move them during the game. `home` is where the character goes when no block is active. |
| `routine` | A list of **shared routine ids** (from `schedules/`) and **blocks**. |
| `stats` | Starting values (others start at their `baseline`). |
| `inventory` | Starting items: `{ item id: count }`. |
| `images` | Optional. Without pictures the character only appears in the "Present" list; with them it is drawn on the map and **clicking it starts a conversation**. `default` = anywhere; `activity.<id>` = while doing that activity; `nodes.<place id>` = a different position (or file) on that map only; `talk.<pose>` = while speaking (a line can pick a pose, `talk.default` otherwise). Files are in the pack's `images/`. |

### Routine blocks

```json
{ "id": "lessons", "days": "weekdays", "from": "08:30", "to": "12:30", "at": "$school", "activity": "study", "priority": 20 }
```

| Field | Default | Meaning |
|---|---|---|
| `days` | all | When the block applies. |
| `from`, `to` | `00:00`–`24:00` | Time range (can cross midnight). |
| `at` | | A place id or `$place`. |
| `activity` | | An activity id from `rules.json`. |
| `priority` | `0` | When blocks overlap, the highest priority wins (on a tie, the first one). |
| `id` | | Name of the block, so effects can replace or remove it. Blocks of a shared routine get the routine's id. |

Characters **plan ahead**: they leave early enough to arrive on time, using the travel times of the links. They live
their routine everywhere, even off screen.

## Shared routines: schedules/&lt;id&gt;.json

A routine that several characters can share (school, shop hours, sleeping at night).

```json
{ "id": "school_timetable",
  "blocks": [ { "days": "weekdays", "from": "08:30", "to": "12:30", "at": "$school", "activity": "study", "priority": 20 } ] }
```

In a character's `routine`, a shared routine can also be written `{ "schedule": "school_timetable", "id": "other_id" }`
to give its blocks another id.

## Items: items/&lt;id&gt;.json

```json
{ "id": "potion_health", "name": "Healing potion", "description": "A thick red liquid in a small flask.",
  "type": "consumable", "value": 25, "tags": [] }
```

`type`: `misc` (default), `consumable`, `equipment`, `key`, `quest`. `value` is the base price. Who owns what is set by
`rules.playerInventory`, `npc.inventory` and the `giveItem` / `takeItem` effects.

## Quests: quests/&lt;id&gt;.json

```json
{ "id": "q_lost_notebook", "title": "The lost notebook", "summary": "Lia lost her notebook.", "giver": "lia",
  "autoStart": { "all": [ { "type": "playerAt", "node": "square_fountain" }, { "type": "time", "from": "08:00", "to": "12:00" } ] },
  "onStart": [ { "do": "message", "text": "New quest: the lost notebook" } ],
  "onComplete": [ { "do": "giveItem", "item": "gold_coin", "count": 20 } ],
  "stages": [
    { "id": "search", "text": "Look for Lia's notebook.",
      "complete": { "type": "hasItem", "item": "notebook" },
      "fail": { "type": "after", "day": 5, "time": "18:00" },
      "onComplete": [ { "do": "message", "text": "Found it!" } ],
      "onFail": [], "next": "return" },
    { "id": "return", "text": "Bring the notebook back to Lia." } ] }
```

| Field | Meaning |
|---|---|
| `title`, `summary`, `giver` | Journal title, summary, character who gives it. |
| `autoStart` | Condition: the quest starts by itself when it becomes true. Without it, use the `startQuest` effect. |
| `onStart`, `onComplete` | Effects. |
| `stages[]` | Steps in order. `text` = journal text; `complete` / `fail` = conditions; `onComplete` / `onFail` = effects; `next` = next stage id or `"end"` (omitted = the following stage). A stage without `complete` only advances with the `advanceQuest` effect (typically from a conversation). |

## Messages: events/&lt;id&gt;.json

A message runs when its trigger happens and its `when` condition is true.

```json
{ "id": "ev_chest", "trigger": "enterNode", "node": "bed_1", "once": true, "cooldownMinutes": 0, "priority": 0,
  "when": { "type": "quest", "quest": "q_chest", "status": "active" },
  "script": [
    { "say": "There is an old chest at the foot of the bed." },
    { "show": "chest", "file": "places/chest.png", "x": 40, "y": 30, "width": 20 },
    { "choices": [
        { "text": "Open it", "when": { "type": "hasItem", "item": "old_key" },
          "then": [ { "do": "giveItem", "item": "gold_coin", "count": 100 },
                    { "do": "completeQuest", "quest": "q_chest" } ] },
        { "text": "Leave it", "then": [] } ] },
    { "hide": "chest" },
    { "if": { "type": "stat", "stat": "happiness", "op": ">=", "value": 60 },
      "then": [ { "say": "You look cheerful today!", "who": "elena", "pose": "happy" } ],
      "else": [ { "say": "Good night.", "who": "elena" } ] } ] }
```

| Field | Default | Meaning |
|---|---|---|
| `trigger` | `enterNode` | `enterNode` (the player arrives in `node`), `talk` (the player talks to `npc`; required), `tick` (every step while the player is in `node`), `wake` (the player wakes up; `node` = where). |
| `node`, `npc` | | Where / with whom. `node` omitted = anywhere. |
| `when` | always | [Condition](#conditions). |
| `once` | `false` | Runs only once per game. |
| `cooldownMinutes` | `0` | Game minutes before it can run again. |
| `priority` | `0` | Only one dialogue opens at a time: the message with the highest priority wins. |
| `script` | | The [steps](#script-steps), run in order. |

Older files may still use `text` + `popup: true` (a single line), `effects` (run before the script) and `choices` at
the top level (a choice at the end, each with its own `effects`). The game still reads them; LivingNPC Studio converts
them to `script` when it saves.

When the player talks to a character and no `talk` message applies, the character answers with a default line.

## Conditions

Used in `when`, `autoStart`, `complete`, `fail`, `requires`, `if` and `choices[].when`.

```
{ "all": [ … ] }   { "any": [ … ] }   { "not": { … } }      a bare array [ … ] means "all"
{ "type": "always" }
{ "type": "playerAt",      "node": "alley" }
{ "type": "npcAt",         "npc": "tomas", "node": "alley" }          without "node" = in the player's place
{ "type": "npcWithPlayer", "npc": "tomas" }
{ "type": "activity",      "npc": "lia", "activity": "study" }
{ "type": "quest",         "quest": "q_id", "status": "notStarted|active|completed|failed", "stage": "stage_id" }
{ "type": "elapsed",       "quest": "q_id", "since": "stage|quest", "days": 2, "hours": 0, "minutes": 0 }
{ "type": "flag",          "key": "door_open", "op": "==", "value": 1 }
{ "type": "stat",          "target": "player|<npc id>", "stat": "happiness", "op": ">=", "value": 50 }
{ "type": "hasItem",       "target": "player|<npc id>", "item": "cellar_key", "op": ">=", "count": 1 }
{ "type": "time",          "from": "20:00", "to": "23:00" }
{ "type": "day",           "days": ["fri", "sat"] }
{ "type": "date",          "fromDay": 3, "toDay": 10 }
{ "type": "after",         "day": 4, "time": "18:00" }
```

- `op` is one of `>=` (default), `>`, `==`, `!=`, `<`, `<=`. `flag` compares with `value` (default 1); a flag that
  was never set is 0. `hasItem` compares with `count` (default 1). `target` omitted = the player.
- `quest`: `status` and `stage` are both optional; `stage` checks the current stage.
- `elapsed`: game time passed since the current stage (`since: "stage"`, default) or the quest (`"quest"`) began.
  True **only while the quest is active**. Inside a quest `quest` can be omitted. The duration is
  `days + hours + minutes` (decimals allowed). Example, "wait two days": a stage with
  `"complete": { "type": "elapsed", "days": 2 }`.
- `date` and `after` count days from 0 (the first day, shown as "Day 1"). `after` is a fixed moment of the calendar.

## Effects

Lists of effects go in `onStart`, `onComplete`, `onFail`, `sleep.effects`, and as steps of a message `script`.

```
{ "do": "message",       "text": "…" }                                   a line in the message log
{ "do": "setFlag",       "key": "x", "value": 1 }                        value default 1
{ "do": "addFlag",       "key": "x", "value": 1 }
{ "do": "startQuest" | "advanceQuest" | "completeQuest" | "failQuest", "quest": "q_id" }
{ "do": "modStat",       "target": "player|<npc>", "stat": "happiness", "delta": 5 }
{ "do": "setStat",       "target": "player|<npc>", "stat": "energy", "value": 100 }
{ "do": "giveItem",      "target": "player|<npc>", "item": "bread", "count": 1 }
{ "do": "takeItem",      "target": "player|<npc>", "item": "bread", "count": 1 }     takes at most what there is
{ "do": "teleportPlayer","node": "square_fountain" }
{ "do": "advanceTime",   "minutes": 30 }
{ "do": "advanceTimeTo", "time": "07:00" }                               until the next 07:00
{ "do": "showLink" | "hideLink" | "openLink" | "closeLink", "link": "alley_to_square" }
{ "do": "moveNpc",       "npc": "voss", "node": "tavern", "forMinutes": 120 }        default 60
{ "do": "setPlace",      "npc": "anselmo", "place": "work", "node": "guild" }
{ "do": "addRoutine",    "npc": "anselmo", "id": "night_watch", "block": { "from": "22:00", "to": "24:00", "at": "$work" } }
{ "do": "removeRoutine", "npc": "anselmo", "id": "church_service" }
{ "do": "setRoutine",    "npc": "anselmo", "routine": [ "sleep_night", { "from": "08:00", "to": "17:00", "at": "$work" } ] }
{ "do": "showImage",     "image": "emma", "file": "emma.png", "x": 10, "y": 5, "width": 40, "height": 60, "layer": 0 }
{ "do": "hideImage",     "image": "emma" }
{ "do": "playSound",     "file": "door.wav", "volume": 0.8 }
{ "do": "startMusic",    "file": "tavern.ogg", "volume": 0.6, "fade": 2 }
{ "do": "stopMusic" }
{ "do": "startAmbient",  "sound": "rain", "file": "rain.ogg", "volume": 0.4 }
{ "do": "stopAmbient",   "sound": "rain" }
```

**Routines during the game.** `setPlace` changes a named place of a character: every block written `$place` follows
it, also in shared routines (moving `work` moves the job; moving `home` moves where it sleeps). `addRoutine` adds
`block`, `blocks`, `schedule` or `routine` (same entries as `npc.routine`); blocks with an `id` that already exists
**replace** those blocks. `removeRoutine` removes the blocks with that id; `setRoutine` replaces the whole routine.
Changes last for the rest of the game and are saved.

**Pictures.** `showImage` keeps a picture on screen – across dialogues and places, and in the saves – until a
`hideImage` with the same `image` name. `file` is in the `images/` folder of the pack that contains the quest or
message. `layer`: 0 = just above the background, 10 = above the characters, 20 = above the links, 30+ = above the
interface; default 40. Same layer = the picture shown later is in front.

**Sounds.** Files are `wav`, `ogg` or `mp3`: `sfx/` for `playSound` and `startAmbient`, `music/` for `startMusic`.
`volume` 0–1 (default 1). There is one music at a time: starting another fades the old one out (`fade` seconds,
default 1.5). Ambient sounds loop above the music and can be layered; the same `sound` name with another file replaces
it. Music and ambient sounds are saved; one-shot sounds are not.

## Script steps

The `script` of a message is a list of steps, run in order. A step is one of:

| Step | Meaning |
|---|---|
| `{ "say": "text", "who": "lia", "pose": "happy" }` | A dialogue line. `who` = a character id or any name; `pose` = a key of the character's `images.talk`. The player clicks to continue. |
| `{ "show": "name", "file": "x.png", "x": 0, "y": 0, "width": 30, "height": 40, "layer": 40 }` | A picture shown **during the dialogue only**. |
| `{ "hide": "name" }` | Hides a picture shown with `show`. |
| `{ "sound": "door.wav", "volume": 1 }` | A sound played together with the next line. |
| `{ "do": "…", … }` | Any [effect](#effects). |
| `{ "if": condition, "then": [steps], "else": [steps] }` | Runs one branch. |
| `{ "choices": [ { "text": "…", "when": condition, "then": [steps] } ] }` | A choice; options whose `when` is false are not shown. |

One step can combine `hide`, `show`, `sound` and `say`: they run in that order. Steps can be nested as deep as needed.

## Patching another pack: $op

A pack can change a definition of another pack (a mod changing the base world) without copying it:

```json
{ "id": "q_lost_notebook", "$op": "merge", "title": "The stolen notebook" }
{ "id": "ev_old_event", "$op": "delete" }
```

- no `$op`: the definition is **replaced** entirely;
- `"$op": "merge"`: the fields are merged into the existing definition (lists are replaced, not appended);
- `"$op": "delete"`: the definition is removed.

A merged character keeps looking for its pictures in its original pack. Give your mod's ids a prefix
(`mymod_dragon_scale`) to avoid clashes.

## Checking your content

- **In the editors:** LivingNPC Studio shows errors and warnings for every quest, message, character, routine, item and
  rule as you type, with the same checks the game does.
- **In the game:** every load writes a report to `<user data>/content_report.txt` (in the Unity editor also
  **LivingNPC ▸ Validate Content**). Each line says the file, the id and the problem. **F5** reloads all files without
  losing the game in progress.

Errors skip the broken part (a link to a place that does not exist, an unknown effect…) and the rest of the world
still loads.

## Rules for authors and AI assistants

1. One file = one thing (a place, a character, a quest…). The `id` inside matches the file name.
2. Use `snake_case` ids and valid JSON. **Never invent ids**: every place, character, item, stat, activity, quest,
   stage and link you reference must exist. Read the existing files first.
3. To change another pack, write a patch with `"$op": "merge"` in your own pack instead of editing the original.
4. Check time logic: can the quest start and advance at that time and place? Are the characters it needs really there
   at that time (look at their routines)?
5. After every change, reload (F5) and read `content_report.txt`. Fix every `[Error]`.
