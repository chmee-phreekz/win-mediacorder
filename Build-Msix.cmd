@echo off
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0tools\Build-Msix.ps1"
pause
