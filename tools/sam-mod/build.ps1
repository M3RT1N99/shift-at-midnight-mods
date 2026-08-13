#Requires -Version 7.0
<#
.SYNOPSIS
    Build sam-mod.exe.

.DESCRIPTION
    Compiles the installer with whichever toolchain is present: clang++ first, then MSVC.
    Both produce a self-contained x64 executable that links only against Windows libraries
    (bcrypt for SHA-256, winhttp for HTTPS), so there is nothing to vendor or restore.

.EXAMPLE
    .\build.ps1
    .\build.ps1 -Configuration Debug
#>
[CmdletBinding()]
param(
    [ValidateSet('Release', 'Debug')][string]$Configuration = 'Release',
    [string]$OutDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = $PSScriptRoot
$source = Join-Path $root 'src/main.cpp'
if (-not (Test-Path -LiteralPath $source)) { throw "Missing $source." }

if (-not $OutDir) { $OutDir = Join-Path $root 'build' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
$output = Join-Path $OutDir 'sam-mod.exe'

function Test-Tool { param([string]$Name) return [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

# clang++ from the Visual Studio toolchain targets x86_64-pc-windows-msvc, which is what we
# want. A 32-bit MinGW g++ would also compile this but produce a 32-bit binary, so g++ is
# deliberately not in the fallback chain.
if (Test-Tool 'clang++') {
    Write-Host "==> clang++ ($Configuration)" -ForegroundColor Cyan

    $target = (& clang++ -dumpmachine).Trim()
    if ($target -notmatch '^x86_64') { throw "clang++ targets '$target'; an x86_64 toolchain is required." }

    $optimisation = if ($Configuration -eq 'Release') { '-O2' } else { '-O0', '-g' }
    $arguments = @('-std=c++20') + $optimisation + @(
        '-Wall', '-Wextra', '-Wno-unused-parameter',
        '-o', $output, $source, '-lbcrypt', '-lwinhttp'
    )

    & clang++ @arguments
    if ($LASTEXITCODE -ne 0) { throw "clang++ failed with exit code $LASTEXITCODE." }
}
elseif (Test-Tool 'cl') {
    Write-Host "==> MSVC ($Configuration)" -ForegroundColor Cyan

    $optimisation = if ($Configuration -eq 'Release') { '/O2' } else { '/Od', '/Zi' }
    $arguments = @('/nologo', '/std:c++20', '/EHsc', '/W3') + $optimisation + @(
        "/Fe:$output", $source,
        '/link', 'bcrypt.lib', 'winhttp.lib'
    )

    Push-Location $OutDir
    try {
        & cl @arguments
        if ($LASTEXITCODE -ne 0) { throw "cl failed with exit code $LASTEXITCODE." }
    }
    finally { Pop-Location }
}
else {
    throw @'
No C++ compiler found.

Install either:
  - Visual Studio 2022 with "Desktop development with C++" (provides cl and clang++), or
  - LLVM/clang for Windows, then run this from a shell where clang++ is on PATH.

Run MSVC builds from a "Developer PowerShell for VS 2022" so the toolchain is on PATH.
'@
}

$built = Get-Item -LiteralPath $output
Write-Host "    $($built.FullName)" -ForegroundColor Green
Write-Host "    $([math]::Round($built.Length / 1KB, 1)) KB" -ForegroundColor Green

# A build that cannot answer --help is not a build worth shipping.
& $output --help | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The built executable did not run.' }
Write-Host '    smoke check passed' -ForegroundColor Green
