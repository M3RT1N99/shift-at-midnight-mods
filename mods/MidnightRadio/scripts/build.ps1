#Requires -Version 7.0
<#
.SYNOPSIS
    Build MidnightRadio and deploy it into the game's Mods folder in one step.

.EXAMPLE
    .\build.ps1                     # Debug build + deploy
    .\build.ps1 -Launch -Tail       # build, deploy, start the game, follow the loader log
    .\build.ps1 -Configuration Release
    .\build.ps1 -TailOnly           # just follow the newest MelonLoader log
#>
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Debug',

    # Point this at the mirror or the real install; the script resolves to the real one.
    [string]$GameDir,

    [switch]$Launch,
    [switch]$Tail,
    [switch]$TailOnly,
    [switch]$Clean,
    [switch]$NoDeploy
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- constants
$ModSlug   = 'MidnightRadio'
$SteamApp  = 3722330
$ExeName   = 'ShiftAtMidnight.exe'
$BuildGuid = '8e59f2b32a5f4d15901aa64b66c56dcf'

$ModRoot   = Split-Path -Parent $PSScriptRoot
$Project   = Join-Path $ModRoot 'src\MidnightRadio.csproj'
$OutDir    = Join-Path $ModRoot "build\$Configuration"
$PayloadML = Join-Path $ModRoot "payload\Mods\$ModSlug"
$PayloadUD = Join-Path $ModRoot "payload\UserData\$ModSlug"

function Write-Step { param([string]$m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok   { param([string]$m) Write-Host "    $m" -ForegroundColor Green }
function Write-Warn { param([string]$m) Write-Host "    ! $m" -ForegroundColor Yellow }

# ------------------------------------------------- locate the real game dir
function Find-SteamGameDir {
    param([int]$AppId, [string]$Exe)

    $libraryRoots = [System.Collections.Generic.List[string]]::new()

    foreach ($key in 'HKCU:\Software\Valve\Steam', 'HKLM:\SOFTWARE\WOW6432Node\Valve\Steam') {
        $steamKey = Get-ItemProperty -Path $key -ErrorAction SilentlyContinue
        if ($null -eq $steamKey) { continue }

        # Valve uses SteamPath for the per-user key and InstallPath for the
        # machine-wide key. Accessing a missing property throws under StrictMode.
        $pathProperty = $steamKey.PSObject.Properties['SteamPath']
        if ($null -eq $pathProperty) {
            $pathProperty = $steamKey.PSObject.Properties['InstallPath']
        }
        if ($null -ne $pathProperty -and $pathProperty.Value) {
            $libraryRoots.Add(($pathProperty.Value -replace '/', '\'))
        }
    }

    foreach ($root in @($libraryRoots)) {
        $vdf = Join-Path $root 'steamapps\libraryfolders.vdf'
        if (-not (Test-Path -LiteralPath $vdf)) { continue }
        foreach ($m in [regex]::Matches((Get-Content -LiteralPath $vdf -Raw), '"path"\s+"([^"]+)"')) {
            $p = $m.Groups[1].Value -replace '\\\\', '\'
            if ($libraryRoots -notcontains $p) { $libraryRoots.Add($p) }
        }
    }

    foreach ($root in $libraryRoots) {
        $acf = Join-Path $root "steamapps\appmanifest_$AppId.acf"
        if (-not (Test-Path -LiteralPath $acf)) { continue }
        $raw = Get-Content -LiteralPath $acf -Raw
        $im  = [regex]::Match($raw, '"installdir"\s+"([^"]+)"')
        if (-not $im.Success) { continue }
        $candidate = Join-Path $root "steamapps\common\$($im.Groups[1].Value)"
        if (Test-Path -LiteralPath (Join-Path $candidate $Exe)) { return $candidate }
    }
    return $null
}

# The project dir is a SYMLINK MIRROR: real directories containing symlinked
# files. Deploying into it creates folders that exist only in the mirror and
# never reach the game. Follow the exe's link to find the true install root.
function Resolve-RealGameDir {
    param([string]$Dir, [string]$Exe)

    $exePath = Join-Path $Dir $Exe
    if (-not (Test-Path -LiteralPath $exePath)) {
        throw "No $Exe under '$Dir'."
    }
    $item = Get-Item -LiteralPath $exePath -Force
    if ($item.LinkType -and $item.LinkTarget) {
        $real = Split-Path -Parent $item.LinkTarget
        Write-Warn "'$Dir' is a symlink mirror -> deploying to '$real' instead."
        return $real
    }
    return $Dir
}

if (-not $GameDir) { $GameDir = Find-SteamGameDir -AppId $SteamApp -Exe $ExeName }
if (-not $GameDir) { throw "Could not locate the game. Pass -GameDir '<path to Shift At Midnight>'." }

$Game = Resolve-RealGameDir -Dir $GameDir -Exe $ExeName
$MlDir     = Join-Path $Game 'MelonLoader'
$MlLogs    = Join-Path $MlDir 'Logs'
$MlInterop = Join-Path $MlDir 'Il2CppAssemblies'
$ModsDest  = Join-Path $Game "Mods\$ModSlug"
$UdDest    = Join-Path $Game "UserData\$ModSlug"

# ------------------------------------------------------------- tail-only
function Get-NewestLog {
    $candidates = @()
    $latest = Join-Path $MlDir 'Latest.log'

    if (Test-Path -LiteralPath $latest -PathType Leaf) {
        $candidates += Get-Item -LiteralPath $latest -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $MlLogs -PathType Container) {
        $candidates += @(Get-ChildItem -LiteralPath $MlLogs -Filter '*.log' -File -ErrorAction SilentlyContinue)
    }

    $candidates |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
}

if ($TailOnly) {
    $log = Get-NewestLog
    if (-not $log) { throw "No logs in '$MlLogs'. Launch the game once with MelonLoader installed." }
    Write-Step "Following $($log.FullName)"
    Get-Content -LiteralPath $log.FullName -Wait -Tail 60
    return
}

# ------------------------------------------------------------- preflight
Write-Step "Game: $Game"

$bootCfg = Join-Path $Game 'ShiftAtMidnight_Data\boot.config'
if (Test-Path -LiteralPath $bootCfg) {
    $guidLine = Select-String -LiteralPath $bootCfg -Pattern '^build-guid=(.+)$' |
                Select-Object -First 1
    if ($guidLine) {
        $found = $guidLine.Matches[0].Groups[1].Value.Trim()
        if ($found -ne $BuildGuid) {
            Write-Warn "build-guid changed: expected $BuildGuid, found $found."
            Write-Warn "The game was updated. Re-verify offsets and let MelonLoader regenerate interop assemblies."
        } else {
            Write-Ok "build-guid matches ($BuildGuid)"
        }
    }
}

$mlAssembly = Join-Path $MlDir 'net6\MelonLoader.dll'
if (-not (Test-Path -LiteralPath $mlAssembly)) {
    throw @"
MelonLoader is not installed in '$Game'.
Install MelonLoader x64 v0.7.3+ (https://github.com/LavaGang/MelonLoader/releases),
point the installer at '$Game\$ExeName', then run this script again.
"@
}

try {
    $mlVersion = [Reflection.AssemblyName]::GetAssemblyName($mlAssembly).Version
} catch {
    throw "Could not read the MelonLoader assembly version from '$mlAssembly': $($_.Exception.Message)"
}
if ($mlVersion -lt [version]'0.7.3') {
    throw "MelonLoader $mlVersion is too old. MidnightRadio requires x64 v0.7.3 or newer."
}
Write-Ok "MelonLoader $mlVersion present"

if (-not (Test-Path -LiteralPath (Join-Path $MlInterop 'Il2Cppmscorlib.dll'))) {
    throw @"
Il2Cpp interop assemblies missing at '$MlInterop'.
Launch the game once with MelonLoader installed and wait for
'Il2Cpp Assembly Generation' to finish, then run this script again.
"@
}
Write-Ok 'Il2Cpp interop assemblies present'

# MelonLoader reads the version from the MelonInfo attribute and nowhere else, so it can
# sit at an old number while the manifest, the package and the mod manager all agree on a
# newer one - which is exactly what shipped in 1.1.0. Fail the build instead.
$manifestVersion = (Get-Content -LiteralPath (Join-Path $ModRoot 'mod.json') -Raw |
                    ConvertFrom-Json).version
$buildVersionFile = Join-Path $ModRoot 'src/BuildVersion.cs'
$declared = [regex]::Match(
    (Get-Content -LiteralPath $buildVersionFile -Raw),
    'Value\s*=\s*"([^"]+)"').Groups[1].Value

if ($declared -ne $manifestVersion) {
    throw "Version mismatch: mod.json says '$manifestVersion' but src/BuildVersion.cs says " +
          "'$declared'. MelonLoader reports the latter, so they must match."
}
Write-Ok "version $manifestVersion consistent"


# ----------------------------------------------------------------- build
if ($Clean -and (Test-Path -LiteralPath $OutDir)) {
    Write-Step 'Cleaning'
    Remove-Item -LiteralPath $OutDir -Recurse -Force
}

Write-Step "Building ($Configuration)"
& dotnet build $Project -c $Configuration -o $OutDir -p:GameDir="$Game" --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed with exit code $LASTEXITCODE." }

$dll = Join-Path $OutDir "$ModSlug.dll"
if (-not (Test-Path -LiteralPath $dll)) { throw "Build produced no $ModSlug.dll in '$OutDir'." }
Write-Ok "$([math]::Round((Get-Item $dll).Length / 1KB, 1)) KB  $ModSlug.dll"

if ($NoDeploy) { Write-Step 'Skipping deploy (-NoDeploy)'; return }

# ---------------------------------------------------------------- deploy
# Refuse to clobber a running game: the loader holds the DLL open.
$running = Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($ExeName)) -ErrorAction SilentlyContinue
if ($running) { throw 'The game is running. Close it before deploying (the DLL is locked).' }

$pdb = Join-Path $OutDir "$ModSlug.pdb"
$manifestSource = Join-Path $PayloadML 'manifest.json'
if (-not (Test-Path -LiteralPath $manifestSource)) {
    throw "Deployment manifest missing at '$manifestSource'."
}

# Stage a complete mod directory beside the destination. Replacing the exact owned
# directory prevents stale libraries or Debug symbols from surviving a Release deploy.
$modsRoot = Split-Path -Parent $ModsDest
New-Item -ItemType Directory -Path $modsRoot -Force | Out-Null
$deployId = [guid]::NewGuid().ToString('N')
$deployStage = Join-Path $modsRoot ".$ModSlug.stage.$deployId"
$deployBackup = Join-Path $modsRoot ".$ModSlug.backup.$deployId"

Write-Step "Staging deployment for $ModsDest"
try {
    New-Item -ItemType Directory -Path $deployStage -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $deployStage 'UserLibs') -Force | Out-Null
    Copy-Item -LiteralPath $dll -Destination $deployStage -Force
    Copy-Item -LiteralPath $manifestSource -Destination $deployStage -Force

    if ($Configuration -eq 'Debug' -and (Test-Path -LiteralPath $pdb)) {
        Copy-Item -LiteralPath $pdb -Destination $deployStage -Force
    }

    $libSrc = Join-Path $PayloadML 'UserLibs'
    if (Test-Path -LiteralPath $libSrc) {
        Get-ChildItem -LiteralPath $libSrc -File | ForEach-Object {
            Copy-Item -LiteralPath $_.FullName `
                -Destination (Join-Path $deployStage 'UserLibs') -Force
        }
    }

    $hadExisting = Test-Path -LiteralPath $ModsDest
    if ($hadExisting) { Move-Item -LiteralPath $ModsDest -Destination $deployBackup }
    try {
        Move-Item -LiteralPath $deployStage -Destination $ModsDest
    } catch {
        if ($hadExisting -and -not (Test-Path -LiteralPath $ModsDest) -and
            (Test-Path -LiteralPath $deployBackup)) {
            Move-Item -LiteralPath $deployBackup -Destination $ModsDest
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

# Seed UserData without ever overwriting the player's own files.
Write-Step "Seeding $UdDest"
foreach ($d in 'Music', 'Cache', 'Cache\_tmp', 'Cache\_ytdlp', 'Tools', 'Logs') {
    New-Item -ItemType Directory -Path (Join-Path $UdDest $d) -Force | Out-Null
}
if (Test-Path -LiteralPath $PayloadUD) {
    Get-ChildItem -LiteralPath $PayloadUD -Recurse -File | ForEach-Object {
        $rel = $_.FullName.Substring($PayloadUD.Length).TrimStart('\')
        $target = Join-Path $UdDest ($rel -replace '\.default$', '')
        if (Test-Path -LiteralPath $target) {
            Write-Warn "keeping existing $rel"
        } else {
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }
    }
}
Write-Ok 'Deployed'

# ---------------------------------------------------------------- launch
if ($Launch) {
    $before = (Get-NewestLog)?.FullName
    Write-Step 'Launching'
    Start-Process -FilePath (Join-Path $Game $ExeName) -WorkingDirectory $Game `
                  -ArgumentList '--melonloader.debug'

    if ($Tail) {
        Write-Step 'Waiting for a new log...'
        $log = $null
        for ($i = 0; $i -lt 120; $i++) {
            Start-Sleep -Milliseconds 500
            $candidate = Get-NewestLog
            if ($candidate -and $candidate.FullName -ne $before) { $log = $candidate; break }
        }
        if (-not $log) { $log = Get-NewestLog }
        if (-not $log) { throw "No MelonLoader log appeared in '$MlLogs'." }
        Write-Step "Following $($log.Name)  (Ctrl+C to stop)"
        Get-Content -LiteralPath $log.FullName -Wait -Tail 40
    }
}
elseif ($Tail) {
    $log = Get-NewestLog
    if ($log) {
        Write-Step "Following $($log.Name)"
        Get-Content -LiteralPath $log.FullName -Wait -Tail 40
    }
}
