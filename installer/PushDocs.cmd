@echo off
setlocal
rem Double-click to upload changes to README.md and the docs folder to GitHub
rem without making a new release (Publish.cmd is for releases, and it uploads
rem these too).

cd /d "%~dp0.."

where git >nul 2>&1
if errorlevel 1 (
    echo Git isn't installed on this PC.
    goto done
)

git add README.md docs || goto failed
git diff --cached --quiet -- README.md docs
if not errorlevel 1 (
    echo Nothing new in README.md or docs to upload.
    goto done
)

git commit -m "Update README" -- README.md docs || goto failed
git push origin HEAD || goto failed
echo.
echo Done. The README on GitHub is up to date.
goto done

:failed
echo.
echo Something went wrong - see above.

:done
echo.
pause
