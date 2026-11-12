# Smoke test fixtures

Test content for `LivingNPC ▸ SmokeTest` and `SoundSmokeTest`: headless games driven from code that check the
simulation, quests, messages, routines, saves and sounds.

- `worlds/smoke_world`: the world the test game runs on (Lia, the square with the fountain, the lost notebook quest,
  Anselmo and his potions…). It is a frozen copy of an early version of the game content, written in Italian, with
  tiny placeholder pictures. Change it only when a test needs a new case.
- `smoke_mods`: packs added on top of that world, one per part of the test: `smoke_routine` (routines changed by
  effects), `smoke_sound` (music, ambient sounds and sounds in messages; the `.wav` files are generated tones: the
  test checks the state, not the noise), `smoke_sequence` (commands in sequence: effects between lines, if / else,
  nested choices), `smoke_auto` (automatic quests: first name, age, sex and relations of two characters, a visit
  template picked by itself, saved and loaded while it runs, `chance`, `startAuto` / `stopAuto`, `did`), `smoke_hud`
  (the bar of pictures: `hud.json` with a missing icon and unknown stats on purpose, a `button` event that rings a
  bell, Lia's hints, Anselmo listed only after two rings, a button hidden by a flag).

From the Unity project folder (`unity/LivingNPC`), with Unity closed (Git Bash or another POSIX shell):

    LIVINGNPC_WORLDS=$PWD/SmokeFixtures/worlds LIVINGNPC_WORLD=smoke_world LIVINGNPC_MODS=$PWD/SmokeFixtures/smoke_mods \
      Unity.exe -batchmode -nographics -projectPath . -executeMethod LivingNPC.EditorTools.ContentMenu.SmokeTest -quit

(and `...ContentMenu.SoundSmokeTest` for the sounds). The result is in `%TEMP%/livingnpc_smoke.txt`
(`livingnpc_sound_smoke.txt`), and the exit code is 0 when every check passes. Without `LIVINGNPC_MODS` the routine,
sound and sequence parts are skipped.
