# LivingNPC Studio

Write the game's quests and messages as Python-like code. The program converts them to the JSON files Unity reads
(and back).

The other tabs edit the rest of the world's data with a form: **Characters** (NPCs and shared routines), **Items**,
**Rules** (`rules.json`) and **System** (`title.json`, the game title, `hud.json`, the bar of pictures, and
`actions.json`, the menu of actions on a character).

## Starting it

Double-click `start.bat` (Node.js is needed), or:

    node server.mjs

and open http://127.0.0.1:5180 (Node.js 20 or newer; no `npm install` needed). The program works on the worlds in `../worlds/<world>/base`
(set `LIVINGNPC_WORLDS` to use another folder, for example a test copy).

## How to use it

- On the left: the **quests** (with their messages, in `events/<quest id>/`), the **automatic quests** (the
  templates the characters pick by themselves, `templates/`, and the shared lines, `lines/`) and the stand-alone
  **messages**.
- In the middle: the code. While you type, errors and warnings (for example a character that does not exist) appear
  below with their line number; the “JSON to be saved” tab shows the files.
- On the right: the buttons. Put the cursor where you want the command and press a button: fill in the form and the
  line is written in the right place (conditions go into the `when:` block by themselves, start settings into the
  properties).
- **Save** (Ctrl+S) writes the JSON files. If you removed or renamed a message, the old file is deleted (it asks
  first).
- **Delete** removes the open document: a quest with all its messages (and the `events/<id>/` folder), a folder of
  messages or one message. It lists the files first and warns if other quests or messages still use the quest.

The **Guide** button explains the syntax with an example. `#` comments are not saved in the JSON.

## The language

```python
event ev_chest:
    trigger = enterNode          # when it starts: enterNode, talk, tick, wake
    node = bedroom
    once = true
    when:                        # start conditions: all true
        quest(q_chest, active)

    say("There is an old chest.")
    show(chest, "chest.png", x=40, y=30)
    choice:
        option "Open it":
            giveItem(gold_coin, 100)
            completeQuest(q_chest)
        option "Leave it":
            pass
    if hasItem(key):
        elena: "You have the key!"
    else:
        elena [sad]: "You need a key."
```

Command and field names are the same as in the JSON (`giveItem` ↔ `{"do": "giveItem"}`), so the conversion loses
nothing. The full list, with the parameters, is in [docs/STUDIO_COMMANDS.md](../docs/STUDIO_COMMANDS.md)
(the table the program uses is `lib/specs.js`).

The game runs the commands of a message **in order** (the `script` list in the JSON, with nested `if` / `else` and
`choices`). Messages written in the old format (`effects`, `popup`, `choices` at the end) are converted to the ordered
list on the first save, with the same behaviour in the game.

## Characters, Items, Rules, System

Every tab has the list on the left, the **Form** in the middle (or the **JSON** view with the file as it is) and on
the right the problems, where it is used and the JSON to be saved. The form edits a copy of the file's object: fields
it does not know stay as they are. The checks say what Unity would say when loading the world; with errors it does not
save.

- **Characters**: name, description and tags; their own places (`places`: home, work… used in the routine as
  `$home`); the routine in blocks (days, times, place, activity, priority) or with shared routines, with the week
  chart and “Where is it?” worked out as in the game (the highest priority wins, on a tie the first block; a block
  that goes past midnight continues the next day); stats and inventory; the images on the map, per activity and when
  talking, with a preview over the background of a place (drag the image to move it) and uploading new pngs to
  `images/`; the **Profile** (their page in the Characters screen of the bar: picture, when they are listed, hints
  that unlock with a condition). **Shared routines** (`schedules/`) are edited in the same tab.
- **Items**: name, description, type, value, tags. Icon, use from the inventory and bonuses are not there because the
  game does not support them yet.
- **Rules**: start time and day, minutes per step and per link, language, starting inventory, stats, activities,
  dayparts, sleep (the effects are written as in the quests, one command per line). The start place is changed in
  the map editor (`project.json`).
- **System** (`title.json`): game title (“Save title” writes it right away to `name` in `project.json`, without
  touching the rest of the file; the world folder keeps its name and does not need to match the title: the game keeps
  the saves by folder), title screen (background image, title drawn or not), title music (on or off, file, volume,
  “Listen”), images of the game buttons and of the main menu buttons (normal, hover / pressed, ends that keep their
  shape, text colour). The preview draws the menu and some buttons as the game draws them; “Upload…” adds pngs to
  `images/` and music to `music/`.
- **System** (`hud.json`): the bar of pictures at the top of the game screen: position, size, the buttons (id, label,
  picture, what they open, key, when they are shown), the My status screen (title, player name, text, picture, which
  stats) and the Characters screen (title, which stats, which details), with a preview of the bar at game size.
- **System** (`actions.json`): the actions of the menu that opens when a character is clicked: id, label (`{npc}` =
  the character), picture, what it does (talk, trade later, or only its messages), for which characters or tags,
  and when it is offered (a condition where `self` is the character); “+ Sub-actions” gives an action its own list,
  opened in a second panel in the game (with how many per page), and the words of the Next / Back / Close buttons. The
  messages of an action are written in Quests & messages with `trigger = action` and `action = <id>`.

Conditions in the forms (when a button is shown, when a character is listed, when a hint appears) are written as in
the quests, one per line, all of them must hold: `quest(crypt, active)`, `hasItem(map)`. A line with a mistake is an
error in the problems and the file keeps the last good condition.

**Delete** warns if quests, messages or characters still use what you are deleting. Renaming an id is not possible
from the form yet: the quests and messages would need updating too.

## Files

- `lib/specs.js`: the commands, the conditions and their parameters (it also drives the buttons).
- `lib/script.js`: JSON → code and code → JSON, with errors per line.
- `lib/docs.js`: which files make up a document and which files saving writes.
- `lib/records.js`: characters, routines, items, rules, title.json, hud.json and actions.json: checks, conditions as lines, routines worked out as in the game,
  where an id is used.
- `lib/store.js`: reading and writing on disk, list of the world's ids.
- `server.mjs`: the local server (127.0.0.1 only).
- `public/`: the page (`app.js` the quests interface, `records.js` the other tabs, `editor.js` the code editor).

## Checks

    node tests/convert.mjs

converts every quest and message of the worlds and of the Unity test packs there and back, plus hand-written examples.

    node tests/records.mjs

checks the routines worked out as in the game, days and times, the sleep effects, and runs the checks on every
character, routine, item and `rules.json` of the worlds and test packs.

    node tools/convert-pack.mjs <pack folder> [--write]

converts a whole pack to the new format (without `--write` it only shows what it would do).
