@echo off
rem LivingNPC Studio: the game's quests and messages written as code.
cd /d "%~dp0"
start "" http://127.0.0.1:5180
node server.mjs
