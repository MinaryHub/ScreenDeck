# Publishes ScreenDeck as a self-contained single exe and builds the installer.
# Output: dist\ScreenDeck.exe and dist\ScreenDeck-Setup-<version>.exe
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

# Start from an empty dist so stale files next to the exe can't mask a missing dependency.
if (Test-Path "$root\dist") { Remove-Item "$root\dist\*" -Recurse -Force }

dotnet publish "$root\src\ScreenDeck" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o "$root\dist"
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install it: winget install JRSoftware.InnoSetup" }

& $iscc /Q "$root\installer\ScreenDeck.iss"
if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

Get-ChildItem "$root\dist\ScreenDeck-Setup-*.exe" | Sort-Object LastWriteTime | Select-Object -Last 1
