@echo off
rem soundsys: first run on real speakers. Plays test sounds, checks everything, writes docs\PHASE0_RESULTS.md.
rem Extra options go straight through, for example: test-hardware.cmd --minutes 3
cd /d "%~dp0"
git pull --ff-only
dotnet run --project src\Soundsys.Diag -c Release -- %*
pause
