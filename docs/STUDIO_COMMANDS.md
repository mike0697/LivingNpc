# LivingNPC Studio – script language reference

LivingNPC Studio lets you write **quests** and **messages** (events) as short, Python-like scripts. Every script is
converted to the JSON files the game reads, and every JSON file can be opened back as a script: nothing is lost in
either direction. Command names are the JSON names (`giveItem(...)` ⇄ `{ "do": "giveItem", ... }`), so this page and
the [JSON reference](JSON_REFERENCE.md) describe the same things from two sides.

> **Alpha:** the language is stable enough to build with, but small details may still change between versions.

## Contents

- [A first example](#a-first-example)
- [How a script is written](#how-a-script-is-written)
- [Quests](#quests)
- [Messages (events)](#messages-events)
- [Automatic quests (template) and shared lines](#automatic-quests-template-and-shared-lines)
- [Dialogue lines](#dialogue-lines)
- [Control flow: if, elif, else, choice](#control-flow-if-elif-else-choice)
- [Game commands (effects)](#game-commands-effects)
- [Dialogue-only commands](#dialogue-only-commands)
- [Conditions](#conditions)
- [Escape hatch: raw(...)](#escape-hatch-raw)
- [Errors and warnings](#errors-and-warnings)

## A first example

```python
quest q_chest:
    title = "The old chest"
    giver = elena
    autoStart:
        playerAt(bed_1)

    stage open:
        text = "Find a way to open the chest in your bedroom."
        complete:
            hasItem(old_key)
        onComplete:
            message("The key turns with a click.")

event ev_chest:
    trigger = enterNode          # when it starts: enterNode, talk, tick, wake
    node = bed_1
    once = true
    when:                        # every condition must be true
        quest(q_chest, active)

    say("There is an old chest at the foot of the bed.")
    show(chest, "places/chest.png", x=40, y=30)
    choice:
        option "Open it" if hasItem(old_key):
            giveItem(gold_coin, 100)
            completeQuest(q_chest)
        option "Leave it":
            pass
    hide(chest)
    if stat(happiness, ">=", 60):
        elena [happy]: "You look cheerful today!"
    else:
        elena: "Good night."
```

## How a script is written

A script is a list of `quest` and `event` blocks. Inside them every line is one of four things:

| Line | Looks like | Example |
|---|---|---|
| **Property** | `name = value` | `title = "The old chest"` |
| **Block** | a name that ends with `:`, with indented lines below | `when:` / `stage open:` / `choice:` |
| **Command** | `name(values)` | `giveItem(bread, 2)` |
| **Dialogue line** | `who: "text"` | `elena: "Hello!"` |

Rules:

- **Indentation** works like Python: a line ending with `:` opens a block, and the lines inside it are indented
  further (4 spaces recommended; a tab counts as 4). All the lines of a block must start at the same column.
- **Comments** start with `#` and go to the end of the line. They are not saved in the JSON.
- **Values** are JSON: text in quotes (`"…"` or `'…'`), numbers, `true`, `false`, `null`, objects `{...}` and lists
  `[...]`. Ids of the world (characters, places, items, quests, stages, stats…) can be written **without quotes**.
  Times can be written as they are: `08:30`.
- **Parameters** can be given by position or by name: `giveItem(bread, 2)` is the same as
  `giveItem(item=bread, count=2)`. Positional values come first, named ones after. The tables below show which
  parameters can be positional (in the signature) and which only by name (`name=`).
- Command and condition names are **not case-sensitive** (`giveitem` works), but the canonical spelling is used when
  the script is written back.
- `pass` is an empty command, for a block that does nothing (`option "Leave it": pass` on two lines).
- Ids can contain letters, digits, `_`, `-` and `.`. A quest or event id becomes its file name.

## Quests

```python
quest q_lost_notebook:
    title = "The lost notebook"          # shown in the journal
    summary = "Lia lost her notebook."   # optional
    giver = lia                          # optional: the character who gives the quest
    autoStart:                           # optional: the quest starts by itself when this becomes true
        playerAt(square_fountain)
        time(08:00, 12:00)
    onStart:                             # commands run when the quest starts
        message("New quest: the lost notebook")
    onComplete:                          # commands run when the quest is completed
        giveItem(gold_coin, 20)

    stage search:
        text = "Look for Lia's notebook."   # journal text of this stage
        complete:                            # the stage ends when this becomes true
            hasItem(notebook)
        fail:                                # optional: the stage (and the quest) fails when this becomes true
            after(5, 18:00)
        onComplete:
            message("Found it!")
        onFail:
            message("Too late...")
        next = return                        # optional: next stage id, or "end" to finish the quest

    stage return:
        text = "Bring the notebook back to Lia."
        # no complete: block → the stage advances with advanceQuest(q_lost_notebook), e.g. from a message
```

| In a quest | Kind | Meaning |
|---|---|---|
| `title`, `summary`, `giver` | properties | Journal title, short summary, character who gives it. |
| `autoStart:` | conditions | Starts the quest by itself. Without it, a `startQuest(...)` command starts it. |
| `onStart:`, `onComplete:` | game commands | Run when the quest starts / is completed. |
| `stage <id>:` | block | One step of the quest, in order. A quest needs at least one stage. |

| In a stage | Kind | Meaning |
|---|---|---|
| `text` | property | Journal text while this stage is active. |
| `onStart:` | game commands | Run when the stage begins. |
| `complete:`, `fail:` | conditions | When the stage is completed / failed. |
| `onComplete:`, `onFail:` | game commands | Run when the stage is completed / failed. |
| `next` | property | Id of the next stage, or `end`. Omitted = the next stage in the list. |

Only **game commands** (see below) can go in `onStart`, `onComplete` and `onFail`: dialogue lines, choices and `if`
belong to messages.

## Automatic quests (template) and shared lines

A `template` is a quest the **characters pick and do by themselves**: every character with *Automatic quests* on (in
the Characters tab, `"autoQuests": true`) that is free looks at the templates it can do, fills their **slots** and
picks one at random, weighted by `weight`. The file goes to `templates/<id>.json`.

```python
template t_bring_bread:
    title = "{self} brings bread to {friend}"
    pauseDays = 1                        # optional: days before the next one (rules.json autoQuests.pauseDays)
    when:                                # optional: who can pick it
        hasItem(bread, target=self)
    weight:                              # optional: the score (base 10)
        base = 5
        stats = {"happiness": 0.2}       # + stat of self × number
        goals = {"help": 30}             # + number, when self has that goal
        relations = {"friend": 0.5}      # + relation of self towards the slot × number
    slot friend:                         # filled when it is picked
        type = npc                       # npc | place | node | item
        free = true                      # npc: not busy with an automatic quest
        when:                            # it = the candidate
            relation(self, it, ">=", 10)
    slot house:
        type = place
        of = friend                      # a named place of a character (default self)
        place = home
    texts:
        intro:
            variant if relation(self, friend, ">=", 30):
                "Off to {friend.first}, with a warm loaf!"
            variant:
                "Maybe {friend} would like some bread."
        talk:                            # said when the player talks to them meanwhile
            "Not now: {friend} is waiting for me in {friend:his|her} room."
    onStart:
        tell(intro)
    onComplete:
        modRelation(friend, self, 10)

    stage go:
        text = "{self} walks to {house}"
        onStart:
            moveNpc(self, house, forMinutes=180)
        complete:
            npcAt(self, house)
        fail:
            elapsed(hours=3)

    stage give:
        complete:
            elapsed(minutes=30)
            chance(80)
        onComplete:
            giveItem(bread, target=friend)
```

| In a template | Kind | Meaning |
|---|---|---|
| `title`, `pauseDays` | properties | Name in the debug panel; days of rest after it. |
| `when:` | conditions | Who can pick it. |
| `weight:` | properties | `base`, `stats`, `goals`, `relations` (see above). A score of 0 or less is never picked. |
| `slot <name>:` | block | `type`, `free`, `of`, `place`, `nodes = [...]`, `items = [...]` and a `when:` with `it`. At most 6. |
| `texts:` | groups | `name:` with lines, or `variant if <condition>:` / `variant:` blocks with lines. |
| `onStart:`, `onComplete:`, `onFail:` | game commands | When it starts, is completed, fails. |
| `stage <id>:` | block | As in a quest. |

Inside a template **`self`** (the character doing it) and **the slot names** can be written wherever a character, a
place or an item goes. The texts say `{self}`, `{friend}`, `{friend.first}`, `{friend.last}`, `{friend.full}`,
`{friend.age}` and `{friend:him|her|them}` (a word for a male, a female, another character). `elapsed(...)` without a
quest counts from the template's stage; `chance(70)` holds for 70% of the times, decided when the stage begins.

**Shared lines** (`lines/<id>.json`) are groups of lines every template can say with `tell(<id>)`; in them `self` is
who speaks:

```python
lines tired:
    variant if stat(energy, "<", 20, target=self):
        "{self.first} yawns."
    variant:
        "{self.first} stretches."
```

## Messages (events)

A message is something that happens in the world: a line of dialogue, a choice, a sound, an item found. It starts
with a **trigger** and only runs when its **when:** conditions are true.

```python
event ev_elena_lesson:
    trigger = talk               # enterNode | talk | tick | wake   (required)
    npc = elena                  # required with talk: the character you talk to
    node = alchimia_1            # optional: only in this place
    once = false                 # optional: true = runs only once per game
    cooldownMinutes = 120        # optional: game minutes before it can run again
    priority = 10                # optional: the highest priority wins when several messages could start
    when:
        activity(elena, work)

    elena: "Hi! I'm in the middle of a lesson."
    modStat(happiness, 1)
```

| Trigger | Starts when… |
|---|---|
| `enterNode` | the player arrives in a place (`node` = which place; omitted = any). |
| `talk` | the player talks to a character (`npc` is required). |
| `tick` | every game step (5 minutes by default) while the player is in `node` (or anywhere). |
| `wake` | the player wakes up after sleeping (`node` = where they wake up, optional). |

Properties go **at the top**, then `when:`, then the commands in the order the game runs them. A message can use
everything: game commands, dialogue lines, images, sounds, `if` and `choice`. Only one dialogue opens at a time, and
time stops while it is open.

In the files, the messages of a quest are kept in `events/<quest id>/`; Studio opens a quest together with its
messages as a single document.

## Dialogue lines

```python
say("The wind howls outside.")          # a line with no speaker (narration)
elena: "Hello!"                         # spoken by a character (id)
elena [happy]: "That's wonderful!"      # with a pose: a key of the character's images.talk
"Old man": "Who goes there?"            # any name in quotes, also not a character
say("Careful.", who=elena, pose=angry)  # the same, written as a command
```

The player clicks (or presses Space / Enter) to go to the next line. `{npc}` inside a text is replaced with the name
of the character the player is talking to.

## Control flow: if, elif, else, choice

```python
if hasItem(lantern):
    say("The lantern lights up the cellar.")
elif time(06:00, 20:00):
    say("Some daylight comes in from the window.")
else:
    say("It is too dark to see anything.")
    advanceTime(30)

choice:
    option "Buy bread (2 gold)" if hasItem(gold_coin, ">=", 2):
        takeItem(gold_coin, 2)
        giveItem(bread)
    option "Ask about the castle":
        bruno: "Nobody goes there anymore."
    option "Leave":
        pass
```

- `if <condition>:` takes **one** condition; combine more with `all(...)`, `any(...)`, `not(...)`.
- `option "Text" if <condition>:` shows the option only when the condition is true.
- `if` and `choice` can be nested as deep as needed, and only exist in messages.

## Game commands (effects)

These work everywhere: in messages and in the quest blocks `onStart`, `onComplete`, `onFail`. The signature shows
the positional parameters; parameters marked `name=` can only be given by name. `target` means a character id or
`player` (omitted = the player).

### Dialogue and journal

| Command | Does |
|---|---|
| `message(text)` | Writes a line in the message log. |

### Quests

| Command | Does |
|---|---|
| `startQuest(quest)` | Starts a quest. |
| `advanceQuest(quest)` | Completes the current stage and goes to the next one. |
| `completeQuest(quest)` | Completes the whole quest. |
| `failQuest(quest)` | Fails the quest. |

### Variables (flags and stats)

| Command | Does |
|---|---|
| `setFlag(key, value)` | Sets a flag (a named number; value omitted = 1). Flags you never set are 0. |
| `addFlag(key, value)` | Adds to a flag (value omitted = 1; can be negative). |
| `modStat(stat, delta, target=)` | Changes a stat by `delta` (e.g. `modStat(happiness, -5, target=lia)`). |
| `setStat(stat, value, target=)` | Sets a stat to `value`. |

### Items

| Command | Does |
|---|---|
| `giveItem(item, count, target=)` | Gives `count` items (omitted = 1). |
| `takeItem(item, count, target=)` | Takes up to `count` items (it never fails: check with `hasItem` first if needed). |

### Images on the map

| Command | Does |
|---|---|
| `showImage(image, file, x=, y=, width=, height=, layer=)` | Shows a picture that **stays** (across dialogues and places) until `hideImage`. `image` is a name you choose; `file` is in the pack's `images/`. Position and size are % of the map area. |
| `hideImage(image)` | Removes the picture with that name. |

`layer` decides what is in front: 0 = just above the background, 10 = above the characters on the map, 20 = above the
links, 30 or more = above the interface. Default 40.

### Sounds and music

| Command | Does |
|---|---|
| `playSound(file, volume=)` | Plays a sound once (file in `sfx/`). |
| `startMusic(file, volume=, fade=)` | Plays music in a loop (file in `music/`), fading out the current one. |
| `stopMusic()` | Stops the music. |
| `startAmbient(sound, file, volume=, fade=)` | Loops an ambient sound (rain, wind, crowd) above the music; `sound` is the name used to stop it. |
| `stopAmbient(sound)` | Stops that ambient sound. |

`volume` goes from 0 to 1 (default 1), `fade` is in seconds (default 1.5). Files can be `wav`, `ogg` or `mp3`.

### World, time and links

| Command | Does |
|---|---|
| `teleportPlayer(node)` | Moves the player to a place at once. |
| `advanceTime(minutes)` | Lets time pass (the world keeps living). |
| `advanceTimeTo(time)` | Lets time pass until the next `HH:mm` (nothing if it is that time already). |
| `showLink(link)` / `hideLink(link)` | Shows / hides a link (a button on the map) by its id. |
| `openLink(link)` / `closeLink(link)` | Opens / closes a link: a closed link is visible but cannot be used. |

### Characters and routines

| Command | Does |
|---|---|
| `moveNpc(npc, node, forMinutes=)` | Sends a character to a place for a while (default 60 minutes), then back to the routine. |
| `setPlace(npc, place, node)` | Changes one of the character's named places (`home`, `work`…). Every routine block written `$place` follows it. |
| `addRoutine(npc, id=, block=)` | Adds routine blocks. Also accepts `routine=`, `blocks=` or `schedule=`. A block with an `id` that already exists replaces it. |
| `removeRoutine(npc, id)` | Removes the routine blocks with that id. |
| `setRoutine(npc, routine)` | Replaces the whole routine (a list, like `routine` in the character file). |

```python
addRoutine(anselmo, id="night_watch", block={"days": "all", "from": "22:00", "to": "24:00", "at": "$work"})
setPlace(anselmo, work, guild)
removeRoutine(anselmo, church_service)
```

Routine changes last for the rest of the game and are kept in the saves.

| Command | Does |
|---|---|
| `modRelation(from, to, delta)` | Changes what `from` thinks of `to` (characters or `player`), from -100 to 100. |
| `setRelation(from, to, value)` | Sets it. |

### Automatic quests

| Command | Does |
|---|---|
| `tell(lines, npc=, everywhere=)` | A line of a text group (of the template, or a file of `lines/`) in the log, said by `npc` (in a template: by `self`). Only when the player is there, unless `everywhere=true`. |
| `setGoal(npc, goal)` | Changes the goal of a character (`goal` omitted or `none` = no goal). |
| `setAutoQuests(npc, on)` | Turns the automatic quests of a character on or off (`on` omitted = true). The running one finishes. |
| `startAuto(npc, template)` | The character starts that template now, if it is free and its slots can be filled. |
| `stopAuto(npc)` | The character drops its automatic quest, with no effects. |

## Dialogue-only commands

These only work inside a message, while its dialogue is open.

| Command | Does |
|---|---|
| `say(text, who=, pose=)` | A dialogue line (see [Dialogue lines](#dialogue-lines)). |
| `show(name, file, x=, y=, width=, height=, layer=)` | Shows a picture **during the dialogue** (it goes away when the dialogue closes). |
| `hide(name)` | Hides a picture shown with `show`. |
| `sound(file, volume=)` | Plays a sound together with the next line. |

## Conditions

Conditions go in `when:`, `autoStart:`, `complete:`, `fail:`, after `if` / `elif`, and after `option "…" if`.
In a block, write **one condition per line: all of them must be true**. Groups:

```python
when:
    playerAt(tavern)                 # all of these...
    any:                             # ...and at least one of these
        time(20:00, 23:59)
        day(["sat", "sun"])
    not:
        flag(tavern_closed)
```

On one line the same groups are written `all(a, b)`, `any(a, b)` and `not(a)`.

| Condition | True when… |
|---|---|
| `always()` | always. |
| `playerAt(node)` | the player is in that place. |
| `npcAt(npc, node)` | the character is in that place (node omitted = with the player). |
| `npcWithPlayer(npc)` | the character is in the same place as the player. |
| `activity(npc, activity)` | the character is doing that activity (an id of `rules.json`). |
| `quest(quest, status, stage)` | the quest is in that state: `notStarted`, `active`, `completed`, `failed`; `stage` (optional) = it is at that stage. |
| `elapsed(quest=, since=, days=, hours=, minutes=)` | that much game time has passed since the current stage (`since="stage"`, default) or the quest (`since="quest"`) began. Only while the quest is active; inside a quest `quest=` can be omitted. |
| `flag(key, op, value)` | compares a flag (op omitted = `>=`, value omitted = 1). |
| `stat(stat, op, value, target=)` | compares a stat of the player or of a character. |
| `hasItem(item, op, count, target=)` | compares how many items someone has (op `>=`, count 1 by default). |
| `time(from, to)` | the time of day is between `from` and `to` (`to` earlier than `from` = across midnight). |
| `day(days)` | the day of the week is one of `days`: `["mon","tue"]`, `"weekdays"`, `"weekend"`, `"all"`. |
| `date(fromDay=, toDay=)` | the game day is between the two (0 = the first day, both included). |
| `after(day, time)` | from that moment of the calendar on (day 0 = the first day; time omitted = 00:00). |
| `relation(from, to, op, value)` | what `from` thinks of `to` compares (characters or `player`). |
| `goal(npc, goal)` | the character has that goal (omitted = any goal). |
| `age(npc, op, value)` / `sex(npc, sex)` | age and sex (`m`, `f`, `x`) of a character. |
| `doing(npc, template, stage)` | the character is doing an automatic quest (that one, at that stage; both optional). |
| `did(npc, template, status, days=)` | it finished one: `completed`, `failed`, `stopped` (optional), in the last `days` (omitted = ever). |
| `chance(percent)` | only in the stages of a template: true for that percent of the times, decided when the stage begins. |

`op` is one of `>=`, `>`, `==`, `!=`, `<`, `<=` and is written in quotes: `flag(gold_given, "==", 0)`.

## Escape hatch: raw(...)

Anything the language has no command for yet can be written as JSON with `raw(...)`; it is saved as it is:

```python
raw({"do": "someNewEffect", "value": 3})
when:
    raw({"type": "someNewCondition"})
```

Studio also writes `raw(...)` when it opens a JSON file with something it cannot show as a command, so the file is
never damaged.

## Errors and warnings

While you type, Studio checks the script like the game will:

- **Errors** (the file cannot be saved): wrong indentation, a line it cannot read, a quest without stages, a message
  without `trigger`, a `talk` message without `npc`, two quests with the same id.
- **Warnings** (it saves, but the game will complain): an id that does not exist in the world (character, place,
  item, quest, stage, link, stat, activity, file), an unknown command or condition, a missing required parameter.

The **JSON to be saved** tab always shows exactly what will be written to disk.
