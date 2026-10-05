# Builds the StartDock installer in one step:
#   1. publishes StartDock.exe (self-contained, single file) to .\publish
#   2. compiles installer\StartDock.iss with Inno Setup
# Output: .\installer\Output\StartDock-Setup-<version>.exe
#
# Needs Inno Setup 6 once:  winget install JRSoftware.InnoSetup
# Close any running StartDock first, or the publish step can't replace the .exe.
#
# Run from PowerShell:  .\build-installer.ps1
# (If scripts are blocked:  powershell -ExecutionPolicy Bypass -File .\build-installer.ps1)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "Publishing StartDock..." -ForegroundColor Cyan
dotnet publish "$root\src\StartDock\StartDock.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

$iscc = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1

if (-not $iscc) {
    throw "Inno Setup 6 not found. Install it with:  winget install JRSoftware.InnoSetup"
}

Write-Host "Building installer..." -ForegroundColor Cyan
& $iscc "$root\installer\StartDock.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed." }

Write-Host ""
Write-Host "Done. Installer is in: $root\installer\Output" -ForegroundColor Green
