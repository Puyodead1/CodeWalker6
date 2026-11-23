@echo off
rem CodeWalker RPF Explorer in GTA VI mode: archives from X:\gta6, keys from the game exe, names from namelists\rpf9_names.tsv
start "" "%~dp0CodeWalker.RPFExplorer\bin\Release\net48\CodeWalker RPF Explorer.exe" "X:\gta6" "--exe=%~dp0..\game_exe\game_scarlett_master.exe" "--names=%~dp0..\namelists\rpf9_names.tsv"
