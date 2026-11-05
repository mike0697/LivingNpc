# Smoke test fixtures

Test content for `LivingNPC ▸ SmokeTest` and `SoundSmokeTest`: headless games driven from code that check the
simulation, quests, messages, routines, saves and sounds.

- `worlds/smoke_world`: the world the test game runs on (Lia, the square with the fountain, the lost notebook quest,
  Anselmo and his potions…). It is a frozen copy of an early version of the game content, written in Italian, with
  tiny placeholder pictures. Change it only when a test needs a new case.
- `smoke_mods`: packs added on top of that world, one per part of the test: `smoke_routine` (routines changed by
  effects), `smoke_sound` (music, ambient sounds and sounds in messages; the `.wav` files are generated tones: the
  test checks the state, not the noise), `smoke_sequence` (commands in sequence: effects between lines, if / else,
  nested choices).

From the Unity project folder (`unity/LivingNPC`), with Unity closed (Git Bash or another POSIX shell):

    LIVINGNPC_WORLDS=$PWD/SmokeFixtures/worlds LIVINGNPC_WORLD=smoke_world LIVINGNPC_MODS=$PWD/SmokeFixtures/smoke_mods \
      Unity.exe -batchmode -nographics -projectPath . -executeMethod LivingNPC.EditorTools.ContentMenu.SmokeTest -quit

(and `...ContentMenu.SoundSmokeTest` for the sounds). The result is in `%TEMP%/livingnpc_smoke.txt`
(`livingnpc_sound_smoke.txt`), and the exit code is 0 when every check passes. Without `LIVINGNPC_MODS` the routine,
sound and sequence parts are skipped.
