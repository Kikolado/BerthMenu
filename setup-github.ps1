# One-time setup: puts this folder on GitHub as a public "BerthMenu" repository.
#
# Needs, once:  winget install GitHub.cli   then   gh auth login
#               winget install --id Git.Git -e
# Run from PowerShell in this folder:
#   powershell -ExecutionPolicy Bypass -File .\setup-github.ps1

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

foreach ($tool in 'git', 'gh') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        $install = if ($tool -eq 'git') { 'winget install --id Git.Git -e' } else { 'winget install GitHub.cli' }
        Write-Host "Stopped: '$tool' isn't installed. Install it with:  $install" -ForegroundColor Red
        Write-Host "then run this script again." -ForegroundColor Red
        exit 1
    }
}

gh auth status; Check "Checking you're signed in to GitHub (run: gh auth login)"
# Uploading the automatic-build file (.github\workflows) needs one extra
# permission that "gh auth login" doesn't ask for. This opens the browser once more.
gh auth refresh -h github.com -s workflow; Check "Adding the workflow permission"
gh auth setup-git; Check "Letting git use your GitHub sign-in"

# The automatic-build file lives in ci\release.yml (Claude can't write into
# .github directly); GitHub needs it in .github\workflows.
New-Item -ItemType Directory -Force .github\workflows | Out-Null
Copy-Item ci\release.yml .github\workflows\release.yml -Force

if (-not (Test-Path .git)) {
    git init -b main; Check "Creating the git repository"
}

# Who the saved versions are credited to: your GitHub name, with GitHub's private
# "noreply" email so your real email isn't published. Only set for this folder.
$login = gh api user --jq .login; Check "Reading your GitHub account"
$id = gh api user --jq .id
if (-not (git config user.name)) { git config user.name $login }
if (-not (git config user.email)) { git config user.email "$id+$login@users.noreply.github.com" }

git add -A; Check "Adding files"
git commit -m "StartDock 0.8"; Check "Saving the first version"

gh repo create BerthMenu --public --source . --remote origin --push `
    --description "A customizable Start Menu replacement for Windows 11."
Check "Creating the GitHub repository"

Write-Host ""
Write-Host "Done: https://github.com/$login/BerthMenu" -ForegroundColor Green
