@echo off
setlocal
rem Double-click to send the current BerthMenu release to winget (see winget.ps1
rem in the folder above). Run it after Publish.cmd, once GitHub has finished
rem building the release.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0..\winget.ps1"
echo.
pause
