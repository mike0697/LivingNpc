# Guide to automatic quests

*[Versione italiana](GUIDA_QUEST_AUTOMATICHE.md)*

In LivingNPC characters can take **initiatives of their own**: bring bread to a friend, go and study in the library,
visit someone they like. The player does not start them: when a character is free, it decides by itself what to do.
These initiatives are called **automatic quests** and they are written as **templates**: a quest written once, in a
generic way, that every suitable character can live in its own way.

This guide explains how they work, how to write a new template, where the lines the characters say are stored, how the
game picks which line to use, and how to add or change them. The full list of fields is in the
[JSON reference](JSON_REFERENCE.md#automatic-quests-templatesidjson) and in the
[Studio language](STUDIO_COMMANDS.md#automatic-quests-template-and-shared-lines).

## Contents

1. [The idea in short](#1-the-idea-in-short)
2. [What the characters need](#2-what-the-characters-need)
3. [The cycle: how a character picks and runs a quest](#3-the-cycle-how-a-character-picks-and-runs-a-quest)
4. [Writing a new template, step by step](#4-writing-a-new-template-step-by-step)
5. [Dialogues: where they are stored](#5-dialogues-where-they-are-stored)
6. [Dialogues: how they are chosen](#6-dialogues-how-they-are-chosen)
7. [Dialogues: writing and changing them](#7-dialogues-writing-and-changing-them)
8. [Busy characters](#8-busy-characters)
9. [Testing and fixing](#9-testing-and-fixing)
10. [Design tips and current limits](#10-design-tips-and-current-limits)

---

## 1. The idea in short

A normal quest is written for the player and names precise characters: *"Elena asks you to go to the crypt"*.
A template instead describes a situation **without knowing who will live it**:

> *A character who has some bread brings it to someone they care about, at their home.*

The template does not say "Elena" or "John": it says **`self`** (whoever does the quest) and some **placeholders**
called *slots* (`friend`; `house`, "their home"). When Elena picks this template, the game fills the slots with real
values: `self` = Elena, `friend` = John, `house` = John's room. From then on the quest is **an instance**, a concrete
quest that goes on stage by stage like a normal one.

The same template, picked by another character, tells a different story. With a few well-written templates the world
feels full of people doing things.

## 2. What the characters need

In the **Characters** tab of the Studio (or in the file `npcs/<id>.json`):

| Field | What it is for |
|---|---|
| **Automatic quests** (`autoQuests`) | The switch: without it the character never picks automatic quests by itself. Off by default. |
| **Goal** (`goal`) | What it wants (`help`, `learn`…). Templates can prefer characters with a certain goal. Goals are declared in **Rules → Goals**. |
| **Relations** (`relations`) | What it thinks of the others, from -100 to 100 (also `player`). They are used to choose who to involve, and quests change them. |
| **First name, Last name, Age, Sex** | Who it is. The dialogues use them: `{friend.first}`, `{friend:him|her}`. |
| **Stats** | Stats (happiness, energy…) can make a template more or less attractive. |

In **Rules → Automatic quests** there are three numbers for the whole world:

| Field | Default | Meaning |
|---|---|---|
| **Check every** (`checkMinutes`) | 60 | Every how many game minutes the free characters look for something to do. |
| **At most at once** (`maxActive`) | 3 | How many automatic quests can run at the same time in the world. It keeps everyone from starting at once. |
| **Pause after one** (`pauseDays`) | 1 | How many days a character rests after finishing one (each template can set its own). |

## 3. The cycle: how a character picks and runs a quest

```
 every "checkMinutes" of game time
        │
        ▼
 is the character free?  ── no ──▶ (follows its routine)
 (switch on, no quest running, pause over,
  not giving a busyGiver quest)
        │ yes
        ▼
 for every template:
   1. does "when" hold for it?          ── no ──▶ out
   2. can the slots be filled?          ── no ──▶ out
   3. score ("weight") above 0?         ── no ──▶ out
        │
        ▼
 random draw weighted by score  (more points = more likely, never certain)
        │
        ▼
 the quest starts: the template's onStart, then the first stage
        │
        ▼
 stages: onStart → (complete / fail) → onComplete / onFail → next stage
        │
        ▼
 end: the template's onComplete or onFail, the character remembers it, then rests "pauseDays"
```

A few important details:

- **Time**: in the game time only moves when the player travels, waits or sleeps. Automatic quests move on with time,
  **even far from the player**: if the player sleeps eight hours, Elena may meanwhile have brought the bread and told
  someone about it.
- **The choice is not fixed**: a template scoring 40 and one scoring 10 are picked about 4 times out of 5 and 1 out of
  5. This makes the characters less predictable.
- **Slots are filled at random** among the valid candidates: if two characters fit as `friend`, one of the two is taken.
- **Characters take turns**: every check starts from a different character, so nobody always comes first when the
  maximum of running quests is reached.
- **Memory**: every character remembers its last 16 automatic quests (which one, how it ended, when, with whom). The
  `did(...)` condition reads it: for example "do not do it again if you already did it this week".
- **Saves**: a running quest is saved with the character (template, stage, slot values) and goes on exactly from there.

## 4. Writing a new template, step by step

### 4.1 Creating the file

In the Studio, **Quests & messages** tab, press **+ Auto quest** and choose an id (for example `t_bring_bread`; it
becomes the file name `templates/t_bring_bread.json`). The Studio writes a working skeleton to start from. Templates
appear in the list on the left, in the **Automatic quests** group, with the letter **A**.

Let's build the whole example of Aetherhall: *Elena brings bread to someone she likes*.

### 4.2 Title and pause

```python
template t_bring_bread:
    title = "{self} brings bread to {friend}"
    pauseDays = 1
```

The title is for you: you see it in the debug panel (F12) with the names already filled in ("Elena brings bread to
John"). `pauseDays` is the rest after this quest; without it the one of the Rules applies.

### 4.3 Who can pick it: `when`

```python
    when:
        hasItem(bread, target=self)
        not:
            any(activity(self, sleep), activity(self, work))
```

The conditions of `when` are about **`self`**: "has some bread, and is neither sleeping nor working". Every line must
be true. If `when` is false the template is not even considered. Slots cannot be used here yet (they are not filled
yet): conditions about other characters go into the slots.

### 4.4 How much it wants it: `weight`

```python
    weight:
        base = 5
        goals = {"help": 30}
        relations = {"friend": 0.5}
```

The score is a sum:

| Entry | Example | Meaning |
|---|---|---|
| `base` | `5` | Starting points (default 10). |
| `stats` | `{"happiness": 0.2}` | + stat of `self` × number (happiness 80 → +16). |
| `goals` | `{"help": 30}` | + 30 if `self` has the goal `help`. |
| `relations` | `{"friend": 0.5}` | + relation of `self` towards the slot × number (relation 40 with John → +20). |

A negative number discourages instead of encouraging (`{"energy": -0.3}`: the more tired, the less likely). If the
total is 0 or less the template is not picked.

### 4.5 Who and what it involves: slots

```python
    slot friend:
        type = npc
        free = true
        when:
            relation(self, it, ">=", 10)
    slot house:
        type = place
        of = friend
        place = home
```

Every slot is a "hole" the game fills when the template is picked. The types:

| `type` | What it becomes | How it is chosen |
|---|---|---|
| `npc` | another character | one at random among those for which `when` holds (never `self`, never a character already in another slot). With `free = true` only free ones ([section 8](#8-busy-characters)). |
| `place` | a place of a character | the place named `place` (`home`, `work`…) of the character `of` (`self` if omitted, or a slot above). |
| `node` | a place of the map | one at random among `nodes = [...]` (all of them, if omitted) for which `when` holds. |
| `item` | an item | one at random among `items = [...]` (all of them, if omitted) for which `when` holds. |

In the `when` of a slot the word **`it`** is the candidate being tried: `relation(self, it, ">=", 10)` means "a
character I feel at least 10 for". A slot can use the slots written **above** it (`house` uses `friend`), not those
below. At most 6 slots.

If a slot finds no candidate, the template is out for that character at that moment.

### 4.6 The stages

```python
    stage go:
        text = "{self} walks to the room of {friend}"
        onStart:
            moveNpc(self, house, forMinutes=180)
        complete:
            npcAt(self, house)
        fail:
            elapsed(hours=3)

    stage give:
        text = "{self} gives the bread to {friend}"
        onStart:
            moveNpc(self, house, forMinutes=60)
        complete:
            elapsed(minutes=30)
        onComplete:
            tell(thanks)
            takeItem(bread, target=self)
            giveItem(bread, target=friend)
            modRelation(friend, self, 10)
            modStat(happiness, 5, target=self)
```

Stages work like those of normal quests, with a few additions:

- **`onStart`** runs when the stage begins: the right place to move the character (`moveNpc` sends it to a place for a
  while, then it goes back to its routine).
- **`complete`** and **`fail`** are conditions checked all the time; the first one that becomes true decides. A stage
  with neither never ends: **always add a timed `fail`** (`elapsed(...)`), so that a stuck character (closed road,
  friend gone) gives up sooner or later.
- **`elapsed(...)`** without a quest counts from the start of the stage (or of the quest, with `since="quest"`).
- **`chance(70)`** holds 70% of the times. The number is drawn **once, when the stage begins**, so it stays the same:
  use it in pairs to split success and failure:

  ```python
      stage study:
          complete:
              elapsed(hours=2)
              chance(70)
          fail:
              elapsed(hours=2)
              not:
                  chance(70)
  ```

- In every command and condition write **`self`** and the **slot names** instead of ids: `giveItem(bread,
  target=friend)`, `npcAt(self, house)`, `modRelation(friend, self, 10)`.
- `next = <stage>` jumps to another stage; without it the following one comes; `next = end` closes the quest.

### 4.7 Start and end of the template

```python
    onStart:
        tell(intro)
    onComplete:
        tell(proud, everywhere=true)
    onFail:
        tell(tired, everywhere=true)
```

The template's `onStart` runs before the first stage. `onComplete` runs when the last stage is completed; `onFail` when
a stage fails (after that stage's own `onFail`). They are the natural place for the **consequences**: relations, stats,
items, flags other quests can read, even `startQuest(...)` of a player quest.

### 4.8 Saving

**Save** (Ctrl+S) in the Studio writes `templates/t_bring_bread.json`. In the game press **F5** to reload the files
without losing the game in progress.

## 5. Dialogues: where they are stored

The lines of the automatic quests live in **two places**:

| Where | What | Who can say them |
|---|---|---|
| Inside the template, in the **`texts:`** block → file `templates/<id>.json`, field `"texts"` | The lines of **that** quest: they can name the slots (`{friend}`, `{house}`). | That template only. |
| In the files **`lines/<id>.json`** (in the Studio: the **Shared lines** document, letter **L**) | **Shared** lines: tiredness, greetings, satisfaction… | Every template, and normal quests and messages too. They only know `self`, not the slots. |

In a template's JSON file the block looks like this:

```json
"texts": {
  "intro": [
    { "when": { "type": "relation", "from": "self", "to": "friend", "op": ">=", "value": 30 },
      "lines": [ "I baked too much bread: {friend.first} will love it.", "Off to {friend}, with a warm loaf!" ] },
    { "lines": [ "Maybe {friend} would like some bread." ] }
  ],
  "thanks": [ "Here, {friend.first}: still warm." ],
  "talk": [ "Not now, dear: I am bringing bread to {friend}." ]
}
```

and a file of shared lines, `lines/proud.json`:

```json
{ "id": "proud", "variants": [
    { "when": { "type": "stat", "stat": "intelligence", "op": ">=", "value": 30, "target": "self" },
      "lines": [ "{self.first} closes the book: another chapter mastered." ] },
    { "lines": [ "{self.first} smiles: the spells finally make sense." ] } ] }
```

Two words used from here on:

- a **group** is a set of lines with a name (`intro`, `thanks`, `talk`, `proud`);
- a **variant** is a part of the group with its own condition (`when`) and its own lines (`lines`).

**Translations**: every line can be a plain text or an object with one entry per language, like every text of the
game: `{ "it": "Vado da {friend}!", "en": "Off to {friend}!" }`. The language shown is the one of `rules.json`.

**In the saves** the line itself is not copied: what is saved is a reference to it (template, group, variant and a code
computed from the text) plus the names of who was in the slots. So the log of a saved game follows your corrections and
the chosen language.

## 6. Dialogues: how they are chosen

### 6.1 When a character speaks

A line is said in two cases.

**With the `tell(group)` command**, written in an `onStart`, `onComplete` or `onFail`:

```python
tell(intro)                       # self says it
tell(thanks, npc=friend)          # the friend says it
tell(proud, everywhere=true)      # written even if the player is not there
```

The line appears in the **message log** at the bottom, as *"Elena: Off to John, with a warm loaf!"*.
**Without `everywhere=true` it appears only if the player is in the same place as the one who speaks**: what happens
far away is not heard. With `everywhere=true` it always arrives (useful for news, rumours, important outcomes).

**When the player talks to a busy character.** If the template has a group called **`talk`** and the player clicks on
the character while it is doing that quest, the character answers with a line of `talk` (*"Not now, dear: I am
bringing bread to John"*). This only happens if no message (`event` with `trigger = talk`) already answers that
character at that moment: hand-written messages come first.

### 6.2 Which group

`tell(intro)` first looks for a group `intro` **in the template**, then for a file **`lines/intro.json`**. So a
template can "cover" a shared line with a more specific version of its own, using the same name.

### 6.3 Which variant

Variants are read **from top to bottom** and **the first one whose condition is true at that moment** wins. A variant
without `when` is always true: put it **last**, as the general case.

```python
        intro:
            variant if relation(self, friend, ">=", 30):       # 1. if it loves them dearly
                "I baked too much bread: {friend.first} will love it."
                "Off to {friend}, with a warm loaf!"
            variant if stat(happiness, "<", 30, target=self):  # 2. if it feels down
                "I'll bring some bread to {friend}. Maybe it cheers me up."
            variant:                                            # 3. otherwise
                "Maybe {friend} would like some bread."
```

Conditions are checked **when the line is said**, not when the quest started: if the character got sad in the
meantime, it speaks sadly. Conditions can use `self` and the slots.

If no variant holds, the character says nothing.

### 6.4 Which line

Inside the winning variant **one line is taken at random**. The "random" depends on the game time, on the group and on
who speaks: in the same minute the same character says the same line, at other times other lines. The more lines a
variant has, the less the character seems to repeat itself.

### 6.5 Placeholders

Before it is shown, the line is filled in with the real names:

| You write | It becomes | Example |
|---|---|---|
| `{self}` | the name of whoever does the quest | Elena |
| `{friend}` | the value of the slot: a character → its name; a place → its title; an item → its name | John / John's room / Bread |
| `{friend.first}` | the first name | John |
| `{friend.last}` | the last name | Hale |
| `{friend.full}` | first and last name | John Hale |
| `{friend.age}` | the age | 15 |
| `{friend:him\|her\|them}` | a word by sex: male, female, other | him |
| `{npc}` | who is speaking (in normal messages: who the player talks to) | Elena |

For the last form the sex comes from the **Sex** field of the character (`m`, `f`, `x`). Without the third word, `x`
uses the first. This is how to write correct sentences: *"{friend:he|she|they} said"*, *"{friend:his|her|their}
room"*; in languages with gendered words it also covers adjectives (Italian *"arrabbiat{friend:o|a}"*).

`{npc.first}`, `{npc:him|her}` and the others also work in normal messages and quests.

## 7. Dialogues: writing and changing them

### 7.1 Adding lines to a template

Open the template in the Studio and write in the `texts:` block. Lines are texts in quotes, one per row.

**A simple group** (one variant, always valid): the lines right below the name.

```python
    texts:
        thanks:
            "Here, {friend.first}: still warm."
            "Fresh from the oven, {friend.first}."
```

**A group with variants**: `variant if <condition>:` blocks and, at the end, `variant:`.

```python
        intro:
            variant if relation(self, friend, ">=", 30):
                "Off to {friend}, with a warm loaf!"
            variant:
                "Maybe {friend} would like some bread."
```

**A translated line**: an object instead of the text.

```python
            {"it": "Vado da {friend}!", "en": "Off to {friend}!"}
```

Then use the group somewhere with `tell(name)` (or call it `talk`) and save.

### 7.2 Shared lines

For a line many templates need, write a `lines` block (in any document; it is saved as `lines/<id>.json` and from then
on it appears in the **Shared lines** document):

```python
lines tired:
    variant if stat(energy, "<", 20, target=self):
        "{self.first} yawns."
        "{self.first} can barely keep {self:his|her} eyes open."
    variant:
        "{self.first} stretches."
```

Shared lines can only use **`self`** (who speaks) and `{npc}`: slots do not exist there, because the line does not know
which template will use it. They can also be said from a normal quest or message, saying who speaks:
`tell(tired, npc=elena)`.

### 7.3 Changing them

Change the text and save (then F5 in the game). Good to know:

- **Messages already in the log of a saved game** that used the old line can no longer be found, and are removed from
  the log when that save is loaded (the rest of the game does not change). The same happens if you move a variant up
  or down.
- **A misspelt placeholder** (`{frend}` instead of `{friend}`) is an error: the Studio does not report it, the game
  does, in the content report (`content_report.txt`, or **LivingNPC ▸ Validate Content** in Unity), with the file and
  the place. Left as it is, the line would show `{frend}` as it is.
- **Curly braces** in the texts of templates are reserved for placeholders: do not use them for anything else.

### 7.4 A trick for believable dialogues

Write the variants thinking of **who** the character is, not only of the situation. Conditions can read its stats, its
relation with the slot, its goal, age, sex, what it did recently (`did`). A template with three well-chosen variants of
three lines each already sounds as if it were written for every character.

## 8. Busy characters

A character is **busy** when:

1. it is doing an automatic quest of its own;
2. it is a slot of someone else's automatic quest (for example John while Elena brings him bread);
3. it gave the player a quest with **`busyGiver = true`** that is still active.

A busy character picks no new automatic quests (cases 1 and 3) and is not taken by slots with **`free = true`**.
Player quests are not blocked by themselves: to keep a busy character from offering a quest, put the **`busy`**
condition in the message that offers it:

```python
quest crypt:
    giver = elena
    busyGiver = true          # while the crypt quest runs, Elena does no automatic quests

event ev_crypt_offer:
    trigger = talk
    npc = elena
    when:
        quest(crypt, notStarted)
        not:
            busy(elena)       # and she does not offer it while she is busy
```

Other useful conditions: `doing(elena, t_bring_bread)` (she is doing that very template, optionally at a given stage)
and `did(elena, t_bring_bread, completed, days=7)` (she finished it in the last 7 days).

## 9. Testing and fixing

**The F12 panel**, the character's page, *Automatic quests* section:

- whether they are on, the goal and the relations (with +10 / -10 buttons);
- the running quest: stage, for how long, the slot values, the `complete` and `fail` conditions evaluated now, branch
  by branch, and a **Stop** button;
- the list of templates with the reason why it could pick each one or not: *can pick it, score 45* — *its when does
  not hold* — *nobody / nothing fits slot friend* — *score 0*; with a **Start** button to start it at once;
- the last quests it did and how they ended.

**The console** (also in F12) takes the same commands as the Studio:

```
setAutoQuests(elena, true)        turns them on / off
startAuto(elena, t_bring_bread)   starts it now (skips when, score and pause)
stopAuto(elena)                   stops it
modRelation(elena, student_1, 20)
setGoal(elena, help)
advanceTime(60)                   lets an hour pass: free characters check every checkMinutes
```

**The content report** lists writing mistakes (unknown slots, misspelt placeholders, undeclared goals) with the file
and the exact place.

## 10. Design tips and current limits

**Tips**

- **Few templates, well filled.** Five templates with rich variants are better than twenty with one line each.
- **Always a timed way out** (`fail: elapsed(...)`) in every stage that waits for something.
- **Respect the routine**: exclude in `when` the moments when the character should be elsewhere (work, sleep), or accept
  that a quest takes it away, if that is the story you want.
- **Leave traces**: relations, stats, items, flags. They make automatic quests visible even when the player was not
  there, and other quests (player quests too) can react to them.
- **Use `everywhere=true` sparingly**: only for outcomes the player should know, or the log fills up.
- **Watch `maxActive`**: with many characters, raise it a little at a time.

**Current limits**

- Characters speak **in the message log**, not in the dialogue window: no player choices inside an automatic quest. To
  involve the player, a template can start a normal quest (`startQuest`) or set a flag a hand-written message uses.
- The player cannot be a slot (the relation with the player can be read and changed, though: `relation(self, player, …)`).
- At most 6 slots per template; every character remembers its last 16 automatic quests.
- The `addRoutine` and `setRoutine` commands need the id of a real character, not `self` or a slot (`moveNpc`,
  `setPlace` and `removeRoutine` accept them).
