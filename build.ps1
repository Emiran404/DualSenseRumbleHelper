# Builds everything: native proxy DLL (zig) + single-file exe into .\dist
$ErrorActionPreference = "Stop"
& (Join-Path $PSScriptRoot "native\build_native.ps1")
& (Join-Path $PSScriptRoot "native\make_icon.ps1")
Push-Location (Join-Path $PSScriptRoot "DualSenseRumbleHelper")
try {
    dotnet publish -c Release -nologo -o (Join-Path $PSScriptRoot "dist")
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }
}
finally { Pop-Location }
Write-Host "Done: $(Join-Path $PSScriptRoot 'dist\DualSenseRumbleHelper.exe')"
