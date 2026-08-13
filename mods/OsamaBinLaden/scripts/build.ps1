#Requires -Version 7.0
<#
.SYNOPSIS
    Build OsamaBinLaden and optionally deploy the owned mod directory atomically.

.EXAMPLE
    .\build.ps1 -NoDeploy
    .\build.ps1 -Configuration Release -NoDeploy
    .\build.ps1 -GameDir 'D:\Games\Shift At Midnight'
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    [string]$GameDir,

    [switch]$Clean,
    [switch]$NoDeploy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$ModSlug = 'OsamaBinLaden'
$SteamApp = 3722330
$ExeName = 'ShiftAtMidnight.exe'
$BuildGuid = '8e59f2b32a5f4d15901aa64b66c56dcf'

$ModRoot = Split-Path -Parent $PSScriptRoot
$Project = Join-Path $ModRoot 'src\OsamaBinLaden.csproj'
$OutDir = Join-Path $ModRoot "build\$Configuration"
$PayloadMod = Join-Path $ModRoot "payload\Mods\$ModSlug"
$PayloadUserData = Join-Path $ModRoot "payload\UserData\$ModSlug"

function Write-Step { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Write-Ok { param([string]$Message) Write-Host "    $Message" -ForegroundColor Green }
function Write-Warn { param([string]$Message) Write-Host "    ! $Message" -ForegroundColor Yellow }

function Find-SteamGameDir {
    param([int]$AppId, [string]$Executable)

    $libraryRoots = [System.Collections.Generic.List[string]]::new()
    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        $steamKey = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($null -eq $steamKey) { continue }

        $pathProperty = $steamKey.PSObject.Properties['SteamPath']
        if ($null -eq $pathProperty) {
            $pathProperty = $steamKey.PSObject.Properties['InstallPath']
        }
        if ($null -ne $pathProperty -and $pathProperty.Value) {
            $libraryRoots.Add(([string]$pathProperty.Value -replace '/', '\'))
        }
    }

    foreach ($root in @($libraryRoots)) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf -PathType Leaf)) { continue }
        $vdfText = Get-Content -LiteralPath $vdf -Raw
        foreach ($match in [regex]::Matches($vdfText, '"path"\s+"([^"]+)"')) {
            $path = $match.Groups[1].Value -replace '\\\\', '\'
            if ($libraryRoots -notcontains $path) { $libraryRoots.Add($path) }
        }
    }

    foreach ($root in $libraryRoots) {
        $appManifest = Join-Path $root "steamapps\appmanifest_$AppId.acf"
        if (-not (Test-Path -LiteralPath $appManifest -PathType Leaf)) { continue }
        $manifestText = Get-Content -LiteralPath $appManifest -Raw
        $installMatch = [regex]::Match($manifestText, '"installdir"\s+"([^"]+)"')
        if (-not $installMatch.Success) { continue }

        $candidate = Join-Path $root "steamapps\common\$($installMatch.Groups[1].Value)"
        if (Test-Path -LiteralPath (Join-Path $candidate $Executable) -PathType Leaf) {
            return $candidate
        }
    }
    return $null
}

function Resolve-RealGameDir {
    param([string]$Directory, [string]$Executable)

    $exePath = Join-Path $Directory $Executable
    if (-not (Test-Path -LiteralPath $exePath -PathType Leaf)) {
        throw "No $Executable under '$Directory'."
    }

    $exeItem = Get-Item -LiteralPath $exePath -Force
    if (($exeItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        if (-not $exeItem.LinkTarget) {
            throw "'$exePath' is a reparse point whose target cannot be resolved. Refusing to build or deploy through it."
        }

        $target = [string]$exeItem.LinkTarget
        if (-not [IO.Path]::IsPathRooted($target)) {
            $target = Join-Path $exeItem.DirectoryName $target
        }
        $resolved = [IO.Path]::GetFullPath($target)
        $realDirectory = Split-Path -Parent $resolved
        if (-not (Test-Path -LiteralPath (Join-Path $realDirectory $Executable) -PathType Leaf)) {
            throw "Resolved executable target '$resolved' does not identify a valid game directory."
        }

        Write-Warn "'$Directory' is a symlink mirror; using '$realDirectory'."
        return $realDirectory
    }

    return [IO.Path]::GetFullPath($Directory)
}

function Assert-NotReparsePoint {
    param([string]$Path, [string]$Description)

    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description '$Path' is a reparse point. Refusing to write through it."
    }
}

if (-not $GameDir) {
    $GameDir = Find-SteamGameDir -AppId $SteamApp -Executable $ExeName
}
if (-not $GameDir) {
    throw "Could not locate the game. Pass -GameDir '<path to Shift At Midnight>'."
}

$Game = Resolve-RealGameDir -Directory $GameDir -Executable $ExeName
$MelonLoaderDir = Join-Path $Game 'MelonLoader'
$InteropDir = Join-Path $MelonLoaderDir 'Il2CppAssemblies'
$ModsRoot = Join-Path $Game 'Mods'
$ModsDestination = Join-Path $ModsRoot $ModSlug
$UserDataDestination = Join-Path (Join-Path $Game 'UserData') $ModSlug

Write-Step "Game: $Game"

$bootConfig = Join-Path $Game 'ShiftAtMidnight_Data\boot.config'
if (Test-Path -LiteralPath $bootConfig -PathType Leaf) {
    $guidLine = Select-String -LiteralPath $bootConfig -Pattern '^build-guid=(.+)$' |
        Select-Object -First 1
    if ($guidLine) {
        $foundGuid = $guidLine.Matches[0].Groups[1].Value.Trim()
        if ($foundGuid -ne $BuildGuid) {
            throw "Game build GUID changed: expected $BuildGuid, found $foundGuid. Re-verify the mod before building."
        }
        Write-Ok "build-guid matches ($BuildGuid)"
    }
}

$melonLoaderAssembly = Join-Path $MelonLoaderDir 'net6\MelonLoader.dll'
if (-not (Test-Path -LiteralPath $melonLoaderAssembly -PathType Leaf)) {
    throw "MelonLoader x64 0.7.3+ is not installed in '$Game'."
}

try {
    $melonLoaderVersion = [Reflection.AssemblyName]::GetAssemblyName($melonLoaderAssembly).Version
} catch {
    throw "Could not read MelonLoader version from '$melonLoaderAssembly': $($_.Exception.Message)"
}
if ($melonLoaderVersion -lt [version]'0.7.3') {
    throw "MelonLoader $melonLoaderVersion is too old; version 0.7.3 or newer is required."
}
Write-Ok "MelonLoader $melonLoaderVersion present"

if (-not (Test-Path -LiteralPath (Join-Path $InteropDir 'Il2Cppmscorlib.dll') -PathType Leaf)) {
    throw "Generated IL2CPP assemblies are missing in '$InteropDir'. Launch the game once with MelonLoader first."
}
Write-Ok 'IL2CPP interop assemblies present'

if ($Clean -and (Test-Path -LiteralPath $OutDir -PathType Container)) {
    Write-Step "Cleaning $OutDir"
    Assert-NotReparsePoint -Path $OutDir -Description 'Build output directory'
    Remove-Item -LiteralPath $OutDir -Recurse -Force
}

Write-Step "Building ($Configuration)"
& dotnet build $Project -c $Configuration -o $OutDir -p:GameDir="$Game" --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build failed with exit code $LASTEXITCODE."
}

$assembly = Join-Path $OutDir "$ModSlug.dll"
if (-not (Test-Path -LiteralPath $assembly -PathType Leaf)) {
    throw "Build produced no $ModSlug.dll in '$OutDir'."
}
Write-Ok "$([math]::Round((Get-Item -LiteralPath $assembly).Length / 1KB, 1)) KB $ModSlug.dll"

if ($NoDeploy) {
    Write-Step 'Skipping deploy (-NoDeploy)'
    return
}

$runningGame = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue
if ($runningGame) {
    throw 'The game is running. Close it before deploying.'
}

$loaderManifest = Join-Path $PayloadMod 'manifest.json'
if (-not (Test-Path -LiteralPath $loaderManifest -PathType Leaf)) {
    throw "Loader manifest missing at '$loaderManifest'."
}

New-Item -ItemType Directory -Path $ModsRoot -Force | Out-Null
Assert-NotReparsePoint -Path $ModsRoot -Description 'Mods directory'
Assert-NotReparsePoint -Path $ModsDestination -Description 'Owned mod directory'

$deployId = [guid]::NewGuid().ToString('N')
$deployStage = Join-Path $ModsRoot ".$ModSlug.stage.$deployId"
$deployBackup = Join-Path $ModsRoot ".$ModSlug.backup.$deployId"
$debugSymbols = Join-Path $OutDir "$ModSlug.pdb"

Write-Step "Staging atomic deployment for $ModsDestination"
try {
    New-Item -ItemType Directory -Path $deployStage | Out-Null
    Copy-Item -LiteralPath $assembly -Destination $deployStage -Force
    Copy-Item -LiteralPath $loaderManifest -Destination $deployStage -Force
    if ($Configuration -eq 'Debug' -and (Test-Path -LiteralPath $debugSymbols -PathType Leaf)) {
        Copy-Item -LiteralPath $debugSymbols -Destination $deployStage -Force
    }

    $hadExisting = Test-Path -LiteralPath $ModsDestination
    if ($hadExisting) {
        Move-Item -LiteralPath $ModsDestination -Destination $deployBackup
    }

    try {
        Move-Item -LiteralPath $deployStage -Destination $ModsDestination
    } catch {
        if ($hadExisting -and
            -not (Test-Path -LiteralPath $ModsDestination) -and
            (Test-Path -LiteralPath $deployBackup)) {
            Move-Item -LiteralPath $deployBackup -Destination $ModsDestination
        }
        throw
    }

    if (Test-Path -LiteralPath $deployBackup) {
        Remove-Item -LiteralPath $deployBackup -Recurse -Force
    }
} finally {
    if (Test-Path -LiteralPath $deployStage) {
        Remove-Item -LiteralPath $deployStage -Recurse -Force
    }
}
Write-Ok 'mod directory deployed atomically'

Assert-NotReparsePoint -Path $UserDataDestination -Description 'Owned UserData directory'
New-Item -ItemType Directory -Path $UserDataDestination -Force | Out-Null

Write-Step "Seeding $UserDataDestination without overwriting user files"
Get-ChildItem -LiteralPath $PayloadUserData -Recurse -File | ForEach-Object {
    $relative = $_.FullName.Substring($PayloadUserData.Length).TrimStart('\')
    $targetRelative = $relative -replace '\.default$', ''
    $target = Join-Path $UserDataDestination $targetRelative
    if (Test-Path -LiteralPath $target) {
        Write-Warn "keeping existing $targetRelative"
        return
    }

    $targetParent = Split-Path -Parent $target
    New-Item -ItemType Directory -Path $targetParent -Force | Out-Null
    Copy-Item -LiteralPath $_.FullName -Destination $target
}
Write-Ok 'Deployed'
