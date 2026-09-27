@echo off
rem Adds Glosa to the Start menu and to "Open with" for the files it plays.
rem The work is done by libs\setup\install.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0libs\setup\install.ps1"
