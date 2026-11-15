# Builds the XInput proxy DLL with zig (portable clang, no Visual Studio needed).
# Usage: .\build_native.ps1 [-Zig <path\to\zig.exe>]
# If zig is not found it is downloaded to .\tools\zig (about 100 MB, one time).
param(
    [string]$Zig = $env:ZIG_EXE,
    [string]$Out = (Join-Path $PSScriptRoot "..\DualSenseRumbleHelper\Resources\xinput_proxy.dll")
)
$ErrorActionPreference = "Stop"
$src = Join-Path $PSScriptRoot "xinput_proxy.c"
$def = Join-Path $PSScriptRoot "xinput_proxy.def"

if (-not $Zig -or -not (Test-Path $Zig)) {
    $cmd = Get-Command zig.exe -ErrorAction SilentlyContinue
    if ($cmd) { $Zig = $cmd.Source }
}
if (-not $Zig -or -not (Test-Path $Zig)) {
    $toolDir = Join-Path $PSScriptRoot "..\tools\zig"
    $found = Get-ChildItem $toolDir -Recurse -Filter zig.exe -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($found) { $Zig = $found.FullName }
}
if (-not $Zig -or -not (Test-Path $Zig)) {
    $toolDir = Join-Path $PSScriptRoot "..\tools\zig"
    New-Item -ItemType Directory -Force $toolDir | Out-Null
    $zip = Join-Path $toolDir "zig.zip"
    Write-Host "zig bulunamadi, indiriliyor (yaklasik 100 MB)..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri "https://ziglang.org/download/0.17.1/zig-x86_64-windows-0.17.1.zip" -OutFile $zip -UseBasicParsing
    Expand-Archive -Path $zip -DestinationPath $toolDir -Force
    Remove-Item $zip
    $Zig = (Get-ChildItem $toolDir -Recurse -Filter zig.exe | Select-Object -First 1).FullName
}

New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
Write-Host "zig: $Zig"
& $Zig cc -shared -O2 -target x86_64-windows-gnu -Wall -Wextra -o $Out $src $def -lkernel32
if ($LASTEXITCODE -ne 0) { throw "zig cc failed with exit code $LASTEXITCODE" }
Write-Host "Built: $Out ($((Get-Item $Out).Length) bytes)"
