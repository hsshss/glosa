@echo off
rem Takes Glosa out of the Start menu and "Open with", as install.cmd put it there.
rem The work is done by libs\setup\uninstall.ps1.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0libs\setup\uninstall.ps1"
