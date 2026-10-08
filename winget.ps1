# Sends the current BerthMenu release to winget, Microsoft's package list, so
# people can install it with:  winget install Kikolado.BerthMenu
#
# Run it after a release has finished on GitHub (Publish.cmd, then wait for the
# build). It fills in the files in the winget folder (version, download link and
# the installer's SHA-256 fingerprint), checks them with winget, and opens a pull
# request on github.com/microsoft/winget-pkgs. The first time, a browser window
# asks you to sign in to GitHub. Microsoft reviews the request, usually within a
# few days; then the new version is on winget.
#
# Double-click installer\Winget.cmd, or run from PowerShell in this folder:
#   powershell -ExecutionPolicy Bypass -File .\winget.ps1

$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

# Pick up programs installed since this window opened (wingetcreate, below).
function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
}
Refresh-Path

function Stop-Here($message) {
    Write-Host "Stopped: $message" -ForegroundColor Red
    exit 1
}

$id = 'Kikolado.BerthMenu'
[xml]$proj = Get-Content "src\BerthMenu\BerthMenu.csproj"
$version = $proj.SelectSingleNode('//Version').InnerText.Trim()
$tag = "v$version"

# The installer's file name and its version in Installed apps both use four
# numbers (1.0 -> 1.0.0.0); winget's version has to match that.
$parts = @($version.Split('.'))
while ($parts.Count -lt 4) { $parts += '0' }
$packageVersion = ($parts[0..3]) -join '.'
$url = "https://github.com/Kikolado/BerthMenu/releases/download/$tag/BerthMenu-Setup-$packageVersion.exe"

Write-Host "BerthMenu $version for winget ($id $packageVersion)"
Write-Host ""

# The fingerprint GitHub's build saved next to the installer.
try {
    $content = (Invoke-WebRequest -UseBasicParsing "$url.sha256").Content
} catch {
    Stop-Here "the $tag release isn't on GitHub yet ($url). Publish it first and wait for GitHub to finish building it."
}
if ($content -is [byte[]]) { $content = [Text.Encoding]::ASCII.GetString($content) }
$sha = ($content.Trim() -split '\s+')[0].ToUpperInvariant()
if ($sha -notmatch '^[0-9A-F]{64}$') { Stop-Here "couldn't read the installer's fingerprint from $url.sha256." }

# Fill in the files from the winget folder (dropping their explanation comments).
$out = Join-Path $PSScriptRoot "winget\out\$packageVersion"
New-Item -ItemType Directory -Force $out | Out-Null
Get-ChildItem $out -Filter *.yaml | Remove-Item
$utf8 = New-Object Text.UTF8Encoding $false
foreach ($file in Get-ChildItem "winget" -Filter *.yaml) {
    $lines = [IO.File]::ReadAllLines($file.FullName) |
        Where-Object { -not ($_.StartsWith('#') -and -not $_.StartsWith('# yaml-language-server')) }
    $text = ($lines -join "`n").Trim() + "`n"
    $text = $text.Replace('{VERSION}', $packageVersion).Replace('{TAG}', $tag).Replace('{URL}', $url).Replace('{SHA256}', $sha)
    [IO.File]::WriteAllText((Join-Path $out $file.Name), $text, $utf8)
}
Write-Host "Manifest written to: $out"

if (-not (Get-Command winget -ErrorAction SilentlyContinue)) {
    Stop-Here "winget isn't on this PC. Install 'App Installer' from the Microsoft Store."
}
$check = winget validate --manifest $out 2>&1 | Out-String
Write-Host $check
if ($check -notmatch 'succeeded') { Stop-Here "winget found a problem in the manifest (see above)." }

if (-not (Get-Command wingetcreate -ErrorAction SilentlyContinue)) {
    Write-Host "Installing wingetcreate (Microsoft's tool for sending packages to winget)..."
    winget install --id Microsoft.WingetCreate --exact --accept-source-agreements --accept-package-agreements
    Refresh-Path
    if (-not (Get-Command wingetcreate -ErrorAction SilentlyContinue)) {
        Stop-Here "wingetcreate didn't install. Close this window and try again."
    }
}

Write-Host ""
$answer = Read-Host "Send $id $packageVersion to winget now? (Y/N)"
if ($answer -notmatch '^[Yy]') {
    Write-Host "Not sent. The files are in $out."
    exit 0
}

wingetcreate submit $out
if ($LASTEXITCODE -ne 0) { Stop-Here "sending to winget (see above)." }

Write-Host ""
Write-Host "Sent. Microsoft's checks and review run on the pull request linked above;" -ForegroundColor Green
Write-Host "once it's merged, 'winget install $id' installs this version." -ForegroundColor Green
