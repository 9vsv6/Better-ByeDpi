<#
.SYNOPSIS
  Downloads the native engines ByeDPI for PC drives into .\deps:
    ciadpi.exe    - byedpi, the DPI-desync SOCKS proxy   (github.com/hufrea/byedpi, MIT)
    tun2socks.exe - TUN -> SOCKS bridge for VPN mode     (github.com/xjasonlyu/tun2socks, GPL-3.0)
    wintun.dll    - WireGuard's TUN driver               (wintun.net, prebuilt DLL licence)
  All three come from the projects' own official release pages.
#>
param(
    [ValidateSet('x64', 'x86', 'arm64')] [string]$Arch = 'x64',
    [string]$ByeDpiVersion = '0.17.3',
    [string]$Tun2SocksVersion = '2.7.0',
    [string]$WintunVersion = '0.14.1'
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$root = Split-Path $PSScriptRoot -Parent
$deps = Join-Path $root 'deps'
$work = Join-Path ([IO.Path]::GetTempPath()) "byedpi-pc-deps-$PID"
New-Item -ItemType Directory -Force $deps, $work | Out-Null

function Get-Zip($url, $name) {
    $zip = Join-Path $work $name
    Write-Host "Downloading $url"
    Invoke-WebRequest -Uri $url -OutFile $zip -UseBasicParsing
    Write-Host ("  sha256 {0}" -f (Get-FileHash $zip -Algorithm SHA256).Hash)
    $dir = Join-Path $work ([IO.Path]::GetFileNameWithoutExtension($name))
    Expand-Archive $zip -DestinationPath $dir -Force
    return $dir
}

try {
    # byedpi only ships x86 builds for Windows; the 32-bit one also runs on ARM64 via emulation.
    $bdArch = if ($Arch -eq 'x64') { 'x86_64' } else { 'i686' }
    $short = $ByeDpiVersion -replace '^0\.', ''
    $dir = Get-Zip "https://github.com/hufrea/byedpi/releases/download/v$ByeDpiVersion/byedpi-$short-$bdArch-w64.zip" 'byedpi.zip'
    $exe = Get-ChildItem $dir -Recurse -Filter '*.exe' | Select-Object -First 1
    Copy-Item $exe.FullName (Join-Path $deps 'ciadpi.exe') -Force

    $tsArch = @{ x64 = 'amd64'; x86 = '386'; arm64 = 'arm64' }[$Arch]
    $dir = Get-Zip "https://github.com/xjasonlyu/tun2socks/releases/download/v$Tun2SocksVersion/tun2socks-windows-$tsArch.zip" 'tun2socks.zip'
    $exe = Get-ChildItem $dir -Recurse -Filter '*.exe' | Select-Object -First 1
    Copy-Item $exe.FullName (Join-Path $deps 'tun2socks.exe') -Force

    $wtArch = @{ x64 = 'amd64'; x86 = 'x86'; arm64 = 'arm64' }[$Arch]
    $dir = Get-Zip "https://www.wintun.net/builds/wintun-$WintunVersion.zip" 'wintun.zip'
    Copy-Item (Join-Path $dir "wintun\bin\$wtArch\wintun.dll") (Join-Path $deps 'wintun.dll') -Force
    Copy-Item (Join-Path $dir 'wintun\LICENSE.txt') (Join-Path $deps 'wintun-LICENSE.txt') -Force

    Write-Host "`nReady in ${deps}:"
    Get-ChildItem $deps | Format-Table Name, Length -AutoSize
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
