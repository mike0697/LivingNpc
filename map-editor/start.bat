@echo off
cd /d "%~dp0"
if not exist node_modules\sharp call npm install
call npm run build

rem Pick the world in the app with "Load World". An argument (start.bat aetherhall) opens it right away.
set "URL=http://localhost:5173"
if not "%~1"=="" set "URL=http://localhost:5173/?world=%~1"

start "" cmd /c "timeout /t 2 /nobreak >nul & start "" "%URL%""
node server.mjs
pause
