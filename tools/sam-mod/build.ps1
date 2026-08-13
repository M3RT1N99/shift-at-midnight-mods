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
if (-not $OutDir) { $OutDir = Join-Path $root 'build' }
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

# Two front ends over the same headers: a console tool and a double-clickable window.
# The GUI is what an end user gets handed, so it is not optional.
$targets = @(
    @{
        Name    = 'sam-mod.exe'
        Source  = Join-Path $root 'src/main.cpp'
        Libs    = @('-lbcrypt', '-lwinhttp')
        MsvcLibs = @('bcrypt.lib', 'winhttp.lib')
        Console = $true
    }
    @{
        Name    = 'sam-mod-gui.exe'
        Source  = Join-Path $root 'src/gui.cpp'
        Libs    = @('-luser32', '-lgdi32', '-lcomctl32', '-lole32', '-lshell32',
                    '-lcomdlg32', '-lbcrypt', '-lwinhttp')
        MsvcLibs = @('user32.lib', 'gdi32.lib', 'comctl32.lib', 'ole32.lib',
                     'shell32.lib', 'comdlg32.lib', 'bcrypt.lib', 'winhttp.lib')
        Console = $false
    }
)

foreach ($t in $targets) {
    if (-not (Test-Path -LiteralPath $t.Source)) { throw "Missing $($t.Source)." }
}

function Test-Tool { param([string]$Name) return [bool](Get-Command $Name -ErrorAction SilentlyContinue) }

# clang++ from the Visual Studio toolchain targets x86_64-pc-windows-msvc, which is what we
# want. A 32-bit MinGW g++ would also compile this but produce a 32-bit binary, so g++ is
# deliberately not in the fallback chain.
if (Test-Tool 'clang++') {
    Write-Host "==> clang++ ($Configuration)" -ForegroundColor Cyan

    $target = (& clang++ -dumpmachine).Trim()
    if ($target -notmatch '^x86_64') { throw "clang++ targets '$target'; an x86_64 toolchain is required." }

    $optimisation = if ($Configuration -eq 'Release') { '-O2' } else { '-O0', '-g' }

    foreach ($t in $targets) {
        $out = Join-Path $OutDir $t.Name
        $arguments = @('-std=c++20') + $optimisation +
            @('-Wall', '-Wextra', '-Wno-unused-parameter', '-o', $out, $t.Source) + $t.Libs

        # A windowed subsystem keeps the GUI from flashing a console behind itself.
        if (-not $t.Console) {
            $arguments += @('-Wl,/SUBSYSTEM:WINDOWS', '-Wl,/ENTRY:wWinMainCRTStartup')
        }

        & clang++ @arguments
        if ($LASTEXITCODE -ne 0) { throw "clang++ failed on $($t.Name) with exit code $LASTEXITCODE." }
    }
}
elseif (Test-Tool 'cl') {
    Write-Host "==> MSVC ($Configuration)" -ForegroundColor Cyan

    $optimisation = if ($Configuration -eq 'Release') { '/O2' } else { '/Od', '/Zi' }

    Push-Location $OutDir
    try {
        foreach ($t in $targets) {
            $out = Join-Path $OutDir $t.Name
            $arguments = @('/nologo', '/std:c++20', '/EHsc', '/W3') + $optimisation +
                @("/Fe:$out", $t.Source, '/link') + $t.MsvcLibs
            if (-not $t.Console) { $arguments += '/SUBSYSTEM:WINDOWS' }

            & cl @arguments
            if ($LASTEXITCODE -ne 0) { throw "cl failed on $($t.Name) with exit code $LASTEXITCODE." }
        }
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

foreach ($t in $targets) {
    $built = Get-Item -LiteralPath (Join-Path $OutDir $t.Name)
    Write-Host "    $($built.Name)  $([math]::Round($built.Length / 1KB, 1)) KB" -ForegroundColor Green
}

# A console build that cannot answer --help is not worth shipping. The GUI has no such
# check here: starting a window from a build script would need one closed again by hand.
$cli = Join-Path $OutDir 'sam-mod.exe'
& $cli --help | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'The built executable did not run.' }
Write-Host '    smoke check passed' -ForegroundColor Green
