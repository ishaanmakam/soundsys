@echo off
rem soundsys: get the latest version, build it, and open the app.
cd /d "%~dp0"
git pull --ff-only
dotnet run --project src\Soundsys.App -c Release
if errorlevel 1 pause
