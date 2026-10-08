# Publishes a new BerthMenu release.
#
# Saves the current code to GitHub and tags it with the version in
# src\BerthMenu\BerthMenu.csproj (<Version>). GitHub then builds the installer
# on its own machines and publishes the release — see .github\workflows\release.yml.
# Your PC doesn't build anything, and BerthMenu can keep running.
#
# Run from PowerShell in this folder:
#   powershell -ExecutionPolicy Bypass -File .\release.ps1

$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

# Pick up programs installed since this PowerShell window opened (like Git or
# the GitHub tool), so a fresh window isn't needed.
$env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")

# Git can be installed without being added to the PATH (a per-user install, or
# the copy bundled with Visual Studio). Look in the usual places and use it.
if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
    $gitExe = @(
        "$env:ProgramFiles\Git\cmd\git.exe",
        "${env:ProgramFiles(x86)}\Git\cmd\git.exe",
        "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe"
    ) + @(Get-ChildItem "$env:ProgramFiles\Microsoft Visual Studio\*\*\Common7\IDE\CommonExtensions\Microsoft\TeamFoundation\Team Explorer\Git\cmd\git.exe" -ErrorAction SilentlyContinue | ForEach-Object FullName) |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if ($gitExe) {
        $env:Path = (Split-Path $gitExe) + ";" + $env:Path
        Write-Host "Using Git from: $gitExe"
    }
}

function Check($what) {
    if ($LASTEXITCODE -ne 0) { Write-Host "Stopped: $what failed (see above)." -ForegroundColor Red; exit 1 }
}

[xml]$proj = Get-Content "src\BerthMenu\BerthMenu.csproj"
$version = $proj.SelectSingleNode('//Version').InnerText
$tag = "v$version"

git fetch --tags --quiet; Check "Checking existing releases"
if (git tag --list $tag) {
    Write-Host "Stopped: $tag was already released. Raise <Version> in BerthMenu.csproj first." -ForegroundColor Red
    exit 1
}

# Pick up any change to the automatic-build file (kept in ci\release.yml).
New-Item -ItemType Directory -Force .github\workflows | Out-Null
Copy-Item ci\release.yml .github\workflows\release.yml -Force

git add -A; Check "Adding changes"
git diff --cached --quiet
if ($LASTEXITCODE -ne 0) {
    git commit -m "BerthMenu $version"; Check "Saving this version"
}

git tag $tag; Check "Tagging $tag"
git push origin HEAD; Check "Uploading the code"
git push origin $tag; Check "Uploading the tag"

$repo = gh repo view --json url --jq .url
Write-Host ""
Write-Host "GitHub is now building $tag. Progress: $repo/actions" -ForegroundColor Green
Write-Host "When it finishes, the release appears at: $repo/releases" -ForegroundColor Green

# Open the build's progress page.
Start-Process "$repo/actions"
