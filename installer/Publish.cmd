@echo off
setlocal
rem Double-click to build and/or publish StartDock.
rem   1. Asks whether to build the installer on this PC (build-installer.ps1).
rem   2. Then, whatever the answer, asks whether to publish to GitHub
rem      (release.ps1: GitHub builds the installer itself and makes the release).
rem The version always comes from src\StartDock\StartDock.csproj, so nothing in
rem this file ever needs changing.

set "ROOT=%~dp0.."
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content '%ROOT%\src\StartDock\StartDock.csproj')).SelectSingleNode('//Version').InnerText"`) do set "VERSION=%%v"
if not defined VERSION set "VERSION=(unknown version)"

echo StartDock %VERSION%
echo.

choice /C YN /M "Build the installer for StartDock %VERSION% on this PC"
if errorlevel 2 goto publish

echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\build-installer.ps1"
if errorlevel 1 (
    echo.
    echo The build failed - see above. If a file was in use, close StartDock and try again.
)
echo.

:publish
choice /C YN /M "Publish StartDock %VERSION% to GitHub"
if errorlevel 2 goto done

echo.
powershell -NoProfile -ExecutionPolicy Bypass -File "%ROOT%\release.ps1"

:done
echo.
pause
