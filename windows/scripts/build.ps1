<#
.SYNOPSIS
  Builds a ready-to-run folder in .\dist: ByeDPI.exe (self-contained, no .NET install needed)
  plus bin\ with the engines. Run scripts\fetch-deps.ps1 once first.
#>
param([ValidateSet('x64', 'x86', 'arm64')] [string]$Arch = 'x64')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
foreach ($f in 'ciadpi.exe', 'tun2socks.exe', 'wintun.dll') {
    if (-not (Test-Path (Join-Path $root "deps\$f"))) { throw "deps\$f is missing - run scripts\fetch-deps.ps1 -Arch $Arch first." }
}
$out = Join-Path $root 'dist'
Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish (Join-Path $root 'src\ByeDpiPc\ByeDpiPc.csproj') -c Release -r "win-$Arch" --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }
Copy-Item (Join-Path $root 'LICENSE') $out
Write-Host "`nDone: $out\ByeDPI.exe"
