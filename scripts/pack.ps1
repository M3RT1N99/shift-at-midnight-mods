#Requires -Version 7.0
<#
.SYNOPSIS
    Pack a mod folder into a distributable .modpkg (a ZIP with a fixed layout).

.DESCRIPTION
    Stages required metadata + docs + payload, enforces an exact executable
    inventory and banned-content checks, writes SHA256SUMS, zips, then verifies
    the round-trip before atomically replacing the final archive and checksum.

.EXAMPLE
    .\pack.ps1 -Mod MidnightRadio
    .\pack.ps1 -Mod MidnightRadio -SkipBuild
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Mod,
    [string]$LibraryRoot = (Split-Path -Parent $PSScriptRoot),
    [string]$OutDir,
    [switch]$SkipBuild,
    [string]$GameDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step { param([string]$m) Write-Host "==> $m" -ForegroundColor Cyan }
function Write-Ok   { param([string]$m) Write-Host "    $m" -ForegroundColor Green }

function Assert-SimpleLeafName {
    param([string]$Value, [string]$Field)

    if ([string]::IsNullOrWhiteSpace($Value) -or
        $Value -notmatch '^[A-Za-z0-9](?:[A-Za-z0-9._-]*[A-Za-z0-9_-])?$' -or
        $Value -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)' -or
        [IO.Path]::GetFileName($Value) -cne $Value) {
        throw "$Field '$Value' must be a simple portable folder/file leaf name."
    }
}

function Get-ContainedFullPath {
    param(
        [string]$RootPath,
        [string]$RelativePath,
        [string]$Description,
        [switch]$AllowRoot
    )

    if ([string]::IsNullOrWhiteSpace($RelativePath) -or [IO.Path]::IsPathRooted($RelativePath)) {
        throw "$Description '$RelativePath' must be a non-empty relative path."
    }
    foreach ($segment in ($RelativePath -split '[\\/]')) {
        if ([string]::IsNullOrEmpty($segment) -or $segment -eq '.' -or $segment -eq '..' -or
            $segment.IndexOfAny([IO.Path]::GetInvalidFileNameChars()) -ge 0 -or
            $segment.EndsWith('.') -or $segment.EndsWith(' ') -or
            $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
            throw "$Description '$RelativePath' contains an unsafe path segment."
        }
    }

    try {
        $rootFull = [IO.Path]::GetFullPath($RootPath)
        $candidate = [IO.Path]::GetFullPath((Join-Path $rootFull $RelativePath))
        $relative = [IO.Path]::GetRelativePath($rootFull, $candidate)
    }
    catch {
        throw "$Description '$RelativePath' is not a valid path: $($_.Exception.Message)"
    }

    $outside = [IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or
               $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal) -or
               $relative.StartsWith("..$([IO.Path]::AltDirectorySeparatorChar)", [StringComparison]::Ordinal)
    if ($outside -or (-not $AllowRoot -and $relative -eq '.')) {
        throw "$Description '$RelativePath' resolves outside its allowed root '$rootFull'."
    }
    return $candidate
}

function Test-IsContainedFullPath {
    param([string]$RootPath, [string]$CandidatePath)

    $rootFull = [IO.Path]::GetFullPath($RootPath)
    $candidateFull = [IO.Path]::GetFullPath($CandidatePath)
    $relative = [IO.Path]::GetRelativePath($rootFull, $candidateFull)
    return -not (
        [IO.Path]::IsPathRooted($relative) -or $relative -eq '..' -or
        $relative.StartsWith("..$([IO.Path]::DirectorySeparatorChar)", [StringComparison]::Ordinal) -or
        $relative.StartsWith("..$([IO.Path]::AltDirectorySeparatorChar)", [StringComparison]::Ordinal)
    )
}

function Get-NormalizedRelativePath {
    param([string]$RootPath, [string]$FullPath)

    if (-not (Test-IsContainedFullPath -RootPath $RootPath -CandidatePath $FullPath)) {
        throw "Path '$FullPath' is outside '$RootPath'."
    }
    return [IO.Path]::GetRelativePath(
        [IO.Path]::GetFullPath($RootPath), [IO.Path]::GetFullPath($FullPath)).Replace('\', '/')
}

function Assert-NoReparsePoints {
    param([string]$Path, [string]$Description)

    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "$Description '$Path' is a reparse point; package inputs must be real files/directories."
    }
    if ($item.PSIsContainer) {
        $link = Get-ChildItem -LiteralPath $Path -Recurse -Force |
                Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 } |
                Select-Object -First 1
        if ($link) {
            throw "$Description contains reparse point '$($link.FullName)'."
        }
    }
}

Assert-SimpleLeafName -Value $Mod -Field 'Mod'
$libraryRootFull = [IO.Path]::GetFullPath($LibraryRoot)
$modsRoot = [IO.Path]::GetFullPath((Join-Path $libraryRootFull 'mods'))
$ModRoot = Get-ContainedFullPath -RootPath $modsRoot -RelativePath $Mod -Description 'Mod'
if (-not (Test-Path -LiteralPath $ModRoot -PathType Container)) {
    throw "No mod folder at '$ModRoot'."
}
if (-not $OutDir) { $OutDir = Join-Path $libraryRootFull 'dist' }

# ------------------------------------------------------------ read manifest
$manifestPath = Join-Path $ModRoot 'mod.json'
if (-not (Test-Path -LiteralPath $manifestPath)) { throw "Missing mod.json in '$ModRoot'." }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json

foreach ($f in 'schema', 'id', 'slug', 'name', 'version', 'payload', 'capabilities') {
    if (-not $manifest.PSObject.Properties.Name.Contains($f)) {
        throw "mod.json is missing required field '$f'."
    }
}
if ($manifest.schema -ne 1) { throw "Unsupported mod.json schema $($manifest.schema); this packer writes schema 1." }
if ($manifest.slug -isnot [string]) { throw 'mod.json slug must be a string.' }
$slug = [string]$manifest.slug
Assert-SimpleLeafName -Value $slug -Field 'mod.json slug'

if ($manifest.version -isnot [string]) { throw 'mod.json version must be a string.' }
$versionText = [string]$manifest.version
try { $parsedVersion = [semver]$versionText }
catch { throw "version '$versionText' is not semver: $($_.Exception.Message)" }
if ($parsedVersion.ToString() -cne $versionText) {
    throw "version '$versionText' is not canonical semver (expected '$parsedVersion')."
}

$pkgName = "$slug-$versionText.modpkg"
Write-Step "Packing $($manifest.name) v$($manifest.version)  ->  $pkgName"

# ------------------------------------------------------------------ build
$builtAssembly = $null
if (-not $SkipBuild) {
    $build = Get-ContainedFullPath -RootPath $ModRoot -RelativePath 'scripts/build.ps1' -Description 'Build script'
    if (-not (Test-Path -LiteralPath $build -PathType Leaf)) {
        throw "Missing build script '$build'. Use -SkipBuild only with an already staged core DLL."
    }
    Assert-NoReparsePoints -Path $build -Description 'Build script'

    Write-Step 'Building Release'
    $buildArgs = @{ Configuration = 'Release'; NoDeploy = $true }
    if ($GameDir) { $buildArgs.GameDir = $GameDir }
    & $build @buildArgs
    if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw 'Build failed.' }

    $builtAssembly = Get-ContainedFullPath -RootPath $ModRoot `
        -RelativePath "build/Release/$slug.dll" -Description 'Build output'
    if (-not (Test-Path -LiteralPath $builtAssembly -PathType Leaf)) {
        throw "Build reported success but produced no '$builtAssembly'."
    }
    Assert-NoReparsePoints -Path $builtAssembly -Description 'Build output'
}

# ------------------------------------------------------------------ stage
$tempRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
$stageName = "modpkg_$([guid]::NewGuid().ToString('N'))"
$stage = Get-ContainedFullPath -RootPath $tempRoot -RelativePath $stageName -Description 'Staging directory'
$tempPackagePath = $null
$tempHashPath = $null

try {
    New-Item -ItemType Directory -Path $stage | Out-Null

    # These files are part of every distributable, not optional decoration.
    $requiredFiles = @('mod.json', 'README.md', 'CHANGELOG.md', 'LICENSE', 'THIRD-PARTY-NOTICES.txt')
    foreach ($fileName in $requiredFiles) {
        $source = Get-ContainedFullPath -RootPath $ModRoot -RelativePath $fileName `
            -Description "Required package file"
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Missing required package file '$fileName'."
        }
        Assert-NoReparsePoints -Path $source -Description "Required package file '$fileName'"
        Copy-Item -LiteralPath $source -Destination $stage -Force
    }

    $payloadSource = Get-ContainedFullPath -RootPath $ModRoot -RelativePath 'payload' `
        -Description 'Payload directory'
    if (-not (Test-Path -LiteralPath $payloadSource -PathType Container)) {
        throw "Missing required payload directory '$payloadSource'."
    }
    Assert-NoReparsePoints -Path $payloadSource -Description 'Payload directory'
    $payloadStage = Get-ContainedFullPath -RootPath $stage -RelativePath 'payload' `
        -Description 'Staged payload directory'
    Copy-Item -LiteralPath $payloadSource -Destination $payloadStage -Recurse -Force

    $docsSource = Get-ContainedFullPath -RootPath $ModRoot -RelativePath 'docs' `
        -Description 'Documentation directory'
    if (Test-Path -LiteralPath $docsSource) {
        if (-not (Test-Path -LiteralPath $docsSource -PathType Container)) {
            throw "Documentation path '$docsSource' is not a directory."
        }
        Assert-NoReparsePoints -Path $docsSource -Description 'Documentation directory'
        $docsStage = Get-ContainedFullPath -RootPath $stage -RelativePath 'docs' `
            -Description 'Staged documentation directory'
        Copy-Item -LiteralPath $docsSource -Destination $docsStage -Recurse -Force
    }

    # The build output goes straight into the disposable stage. Packaging never mutates
    # payload/ in the source tree. -SkipBuild deliberately uses an already staged DLL.
    $entryAssembly = "payload/Mods/$slug/$slug.dll"
    $entryAssemblyPath = Get-ContainedFullPath -RootPath $stage -RelativePath $entryAssembly `
        -Description 'Core assembly'
    if ($builtAssembly) {
        New-Item -ItemType Directory -Path (Split-Path -Parent $entryAssemblyPath) -Force | Out-Null
        Copy-Item -LiteralPath $builtAssembly -Destination $entryAssemblyPath -Force
        Write-Ok "staged $slug.dll"
    }
    if (-not (Test-Path -LiteralPath $entryAssemblyPath -PathType Leaf)) {
        throw "Missing core assembly '$entryAssembly'. Build the mod before packing."
    }

    $modPayloadRoot = Get-ContainedFullPath -RootPath $stage `
        -RelativePath "payload/Mods/$slug" -Description 'Mod payload directory'
    if (-not (Test-Path -LiteralPath $modPayloadRoot -PathType Container)) {
        throw "Missing mod payload directory 'payload/Mods/$slug'."
    }
    $loaderManifestPath = Get-ContainedFullPath -RootPath $stage `
        -RelativePath "payload/Mods/$slug/manifest.json" -Description 'Loader manifest'
    if (-not (Test-Path -LiteralPath $loaderManifestPath -PathType Leaf)) {
        throw "Missing loader manifest 'payload/Mods/$slug/manifest.json'."
    }

    # Exact inventory for Mods/: the core assembly and loader manifest are implicit;
    # bundled library binaries and licence files are allowed only when declared verbatim.
    $allowedModFiles = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $allowedBinaryFiles = [System.Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    $entryAssemblyRel = Get-NormalizedRelativePath -RootPath $stage -FullPath $entryAssemblyPath
    $loaderManifestRel = Get-NormalizedRelativePath -RootPath $stage -FullPath $loaderManifestPath
    [void]$allowedModFiles.Add($entryAssemblyRel)
    [void]$allowedModFiles.Add($loaderManifestRel)
    [void]$allowedBinaryFiles.Add($entryAssemblyRel)

    if ($manifest.PSObject.Properties.Name.Contains('bundledLibraries') -and
        $null -ne $manifest.bundledLibraries) {
        foreach ($library in @($manifest.bundledLibraries)) {
            if ($null -eq $library) { throw 'mod.json contains a null bundledLibraries entry.' }
            $libraryName = '<unnamed>'
            if ($library.PSObject.Properties.Name.Contains('name') -and
                -not [string]::IsNullOrWhiteSpace([string]$library.name)) {
                $libraryName = [string]$library.name
            }
            foreach ($field in 'path', 'licenseFile') {
                if (-not $library.PSObject.Properties.Name.Contains($field) -or
                    [string]::IsNullOrWhiteSpace([string]$library.$field)) {
                    throw "Bundled library '$libraryName' is missing '$field' in mod.json."
                }
            }

            $libraryPath = Get-ContainedFullPath -RootPath $stage `
                -RelativePath ([string]$library.path) -Description "Bundled library '$libraryName' path"
            $licensePath = Get-ContainedFullPath -RootPath $stage `
                -RelativePath ([string]$library.licenseFile) -Description "Bundled library '$libraryName' licence"
            foreach ($declared in @($libraryPath, $licensePath)) {
                if (-not (Test-IsContainedFullPath -RootPath $modPayloadRoot -CandidatePath $declared)) {
                    throw "Bundled library '$libraryName' declares a file outside 'payload/Mods/$slug'."
                }
                if (-not (Test-Path -LiteralPath $declared -PathType Leaf)) {
                    throw "Bundled library '$libraryName' declares missing file '$declared'."
                }
            }

            $libraryRel = Get-NormalizedRelativePath -RootPath $stage -FullPath $libraryPath
            $licenseRel = Get-NormalizedRelativePath -RootPath $stage -FullPath $licensePath
            if ($allowedModFiles.Contains($libraryRel) -or
                -not $allowedBinaryFiles.Add($libraryRel)) {
                throw "Bundled library '$libraryName' reuses binary path '$libraryRel'."
            }
            if ($allowedBinaryFiles.Contains($licenseRel) -or $licenseRel -ieq $loaderManifestRel) {
                throw "Bundled library '$libraryName' licence path '$licenseRel' is not a distinct licence file."
            }
            [void]$allowedModFiles.Add($libraryRel)
            [void]$allowedModFiles.Add($licenseRel)
        }
    }

    # Every declared package source must remain inside the stage after canonicalization.
    if ($null -eq $manifest.payload -or $manifest.payload -is [string]) {
        throw 'mod.json payload must be an array of payload declarations.'
    }
    foreach ($payload in @($manifest.payload)) {
        if ($null -eq $payload) { throw 'mod.json contains a null payload entry.' }
        $payloadId = '<unnamed>'
        if ($payload.PSObject.Properties.Name.Contains('id') -and
            -not [string]::IsNullOrWhiteSpace([string]$payload.id)) {
            $payloadId = [string]$payload.id
        }
        if (-not $payload.PSObject.Properties.Name.Contains('src') -or
            [string]::IsNullOrWhiteSpace([string]$payload.src)) {
            throw "Declared payload '$payloadId' is missing 'src'."
        }
        if (-not $payload.PSObject.Properties.Name.Contains('required') -or
            $payload.required -isnot [bool]) {
            throw "Declared payload '$payloadId' must have a boolean 'required' field."
        }

        $declaredSource = Get-ContainedFullPath -RootPath $stage `
            -RelativePath ([string]$payload.src) -Description "Declared payload '$payloadId' source"
        $sourceExists = Test-Path -LiteralPath $declaredSource
        if ($payload.required -and -not $sourceExists) {
            throw "Declared payload '$payloadId' missing: $($payload.src)"
        }
        if ($sourceExists -and $payload.PSObject.Properties.Name.Contains('kind')) {
            if ($payload.kind -eq 'directory' -and
                -not (Test-Path -LiteralPath $declaredSource -PathType Container)) {
                throw "Declared payload '$payloadId' must be a directory: $($payload.src)"
            }
            if ($payload.kind -eq 'file' -and
                -not (Test-Path -LiteralPath $declaredSource -PathType Leaf)) {
                throw "Declared payload '$payloadId' must be a file: $($payload.src)"
            }
        }
    }
    Write-Ok 'payload declarations resolve inside staging root'

    # ----------------------------------------------------- CONTENT GUARD
    # Exact inventory protects executable content. Extension and signature checks then
    # reject common media/game assets even when they have been renamed.
    Write-Step 'Content guard'

    $bannedExt = @(
        '.mp3','.ogg','.wav','.flac','.m4a','.aac','.opus','.wma','.mp4','.webm','.aiff','.mid',
        '.assets','.resource','.ress','.bundle','.unity3d','.sharedassets','.dat','.acf'
    )
    $bannedName = @(
        'GameAssembly.dll','UnityPlayer.dll','baselib.dll','global-metadata.dat',
        'ShiftAtMidnight.exe','MelonLoader.dll','0Harmony.dll','version.dll','winhttp.dll',
        'yt-dlp.exe','ffmpeg.exe','ffprobe.exe'
    )
    $binaryExtensions = @('.dll','.exe','.com','.scr','.cpl')
    $magics = @(
        @{ Offset = 0; Bytes = [byte[]](0x4F,0x67,0x67,0x53);      Name = 'Ogg' }
        @{ Offset = 0; Bytes = [byte[]](0x49,0x44,0x33);           Name = 'MP3/ID3' }
        @{ Offset = 0; Bytes = [byte[]](0x52,0x49,0x46,0x46);      Name = 'RIFF media' }
        @{ Offset = 0; Bytes = [byte[]](0x66,0x4C,0x61,0x43);      Name = 'FLAC' }
        @{ Offset = 0; Bytes = [byte[]](0x55,0x6E,0x69,0x74,0x79); Name = 'UnityFS bundle' }
        @{ Offset = 0; Bytes = [byte[]](0x89,0x50,0x4E,0x47);      Name = 'PNG image' }
        @{ Offset = 0; Bytes = [byte[]](0xFF,0xD8,0xFF);           Name = 'JPEG image' }
        @{ Offset = 0; Bytes = [byte[]](0x47,0x49,0x46,0x38);      Name = 'GIF image' }
        @{ Offset = 4; Bytes = [byte[]](0x66,0x74,0x79,0x70);      Name = 'ISO media container' }
    )

    $violations = [System.Collections.Generic.List[string]]::new()
    $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File -Force)
    foreach ($file in $files) {
        $rel = Get-NormalizedRelativePath -RootPath $stage -FullPath $file.FullName
        if ($rel.StartsWith('payload/Mods/', [StringComparison]::OrdinalIgnoreCase) -and
            -not $allowedModFiles.Contains($rel)) {
            $violations.Add("$rel  (unexpected file under payload/Mods; declare libraries explicitly)")
        }
        if ($bannedExt -contains $file.Extension.ToLowerInvariant()) {
            $violations.Add("$rel  (banned extension '$($file.Extension)')")
        }
        if ($bannedName -contains $file.Name) {
            $violations.Add("$rel  (must never be redistributed)")
        }

        $head = [byte[]]::new(16)
        $stream = [IO.File]::OpenRead($file.FullName)
        try { $read = $stream.Read($head, 0, $head.Length) } finally { $stream.Dispose() }

        $looksLikePe = $read -ge 2 -and $head[0] -eq 0x4D -and $head[1] -eq 0x5A
        if (($looksLikePe -or $binaryExtensions -contains $file.Extension.ToLowerInvariant()) -and
            -not $allowedBinaryFiles.Contains($rel)) {
            $violations.Add("$rel  (undeclared executable/library content)")
        }
        if ($read -ge 2 -and $head[0] -eq 0xFF -and ($head[1] -band 0xE0) -eq 0xE0) {
            $violations.Add("$rel  (content looks like an MPEG/AAC audio frame)")
        }
        foreach ($magic in $magics) {
            $signature = $magic.Bytes
            $offset = [int]$magic.Offset
            if ($read -lt $offset + $signature.Length) { continue }
            $matches = $true
            for ($i = 0; $i -lt $signature.Length; $i++) {
                if ($head[$offset + $i] -ne $signature[$i]) { $matches = $false; break }
            }
            if ($matches) { $violations.Add("$rel  (content looks like $($magic.Name))") }
        }
    }

    if ($violations.Count -gt 0) {
        throw "Content guard FAILED:`n  " + ($violations -join "`n  ")
    }
    Write-Ok "$($files.Count) files passed inventory and banned-content checks"

    # ------------------------------------------------------- SHA256SUMS
    Write-Step 'Hashing'
    $lines = [System.Collections.Generic.List[string]]::new()
    Get-ChildItem -LiteralPath $stage -Recurse -File -Force |
        Sort-Object { Get-NormalizedRelativePath -RootPath $stage -FullPath $_.FullName } |
        ForEach-Object {
            $rel = Get-NormalizedRelativePath -RootPath $stage -FullPath $_.FullName
            $hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
            $lines.Add("$hash  $rel")
        }
    $sumsPath = Get-ContainedFullPath -RootPath $stage -RelativePath 'SHA256SUMS' `
        -Description 'Integrity index'
    $sumsContent = ($lines -join "`n") + "`n"
    [IO.File]::WriteAllText($sumsPath, $sumsContent, [Text.UTF8Encoding]::new($false))
    Write-Ok "$($lines.Count) entries"

    # Build both output files under unique names in the final directory. Existing release
    # files remain untouched until the new archive has passed the complete round trip.
    New-Item -ItemType Directory -Path $OutDir -Force | Out-Null
    if (-not (Test-Path -LiteralPath $OutDir -PathType Container)) {
        throw "Output path '$OutDir' is not a directory."
    }
    $outDirFull = [IO.Path]::GetFullPath($OutDir)
    $pkgPath = Get-ContainedFullPath -RootPath $outDirFull -RelativePath $pkgName `
        -Description 'Package output'
    $hashPath = Get-ContainedFullPath -RootPath $outDirFull -RelativePath "$pkgName.sha256" `
        -Description 'Package checksum output'
    $outputToken = [guid]::NewGuid().ToString('N')
    $tempPackagePath = Get-ContainedFullPath -RootPath $outDirFull `
        -RelativePath ".$pkgName.$outputToken.tmp" -Description 'Temporary package output'
    $tempHashPath = Get-ContainedFullPath -RootPath $outDirFull `
        -RelativePath ".$pkgName.sha256.$outputToken.tmp" -Description 'Temporary checksum output'
    $backupPackagePath = Get-ContainedFullPath -RootPath $outDirFull `
        -RelativePath ".$pkgName.$outputToken.backup" -Description 'Package rollback backup'
    $backupHashPath = Get-ContainedFullPath -RootPath $outDirFull `
        -RelativePath ".$pkgName.sha256.$outputToken.backup" -Description 'Checksum rollback backup'

    Write-Step 'Compressing'
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    [IO.Compression.ZipFile]::CreateFromDirectory(
        $stage, $tempPackagePath, [IO.Compression.CompressionLevel]::Optimal, $false)

    # ----------------------------------------------------------- verify
    Write-Step 'Verifying archive'
    $expected = @{}
    foreach ($line in $lines) {
        $expectedHash, $expectedPath = $line -split '  ', 2
        $expected[$expectedPath] = $expectedHash
    }

    $zip = [IO.Compression.ZipFile]::OpenRead($tempPackagePath)
    try {
        $seen = 0
        $sumsSeen = 0
        foreach ($entry in $zip.Entries) {
            if ($entry.FullName.EndsWith('/')) { continue }
            if ($entry.FullName -eq 'SHA256SUMS') {
                $reader = [IO.StreamReader]::new($entry.Open())
                try { $archivedSums = $reader.ReadToEnd() } finally { $reader.Dispose() }
                if ($archivedSums -cne $sumsContent) {
                    throw 'Archive SHA256SUMS content differs from the generated integrity index.'
                }
                $sumsSeen++
                continue
            }
            if (-not $expected.ContainsKey($entry.FullName)) {
                throw "Archive entry not covered by SHA256SUMS: $($entry.FullName)"
            }
            $stream = $entry.Open()
            try {
                $sha = [Security.Cryptography.SHA256]::Create()
                try { $actualHash = ($sha.ComputeHash($stream) | ForEach-Object { $_.ToString('x2') }) -join '' }
                finally { $sha.Dispose() }
            }
            finally { $stream.Dispose() }
            if ($actualHash -ne $expected[$entry.FullName]) {
                throw "Hash mismatch for $($entry.FullName)"
            }
            $seen++
        }
        if ($sumsSeen -ne 1) { throw "Archive contains $sumsSeen SHA256SUMS entries; expected exactly one." }
        if ($seen -ne $expected.Count) {
            throw "SHA256SUMS lists $($expected.Count) files but the archive holds $seen."
        }
    }
    finally { $zip.Dispose() }

    $pkgHash = (Get-FileHash -LiteralPath $tempPackagePath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($tempHashPath,
        "$pkgHash  $pkgName`n", [Text.UTF8Encoding]::new($false))

    # File.Replace/Move are same-directory atomic operations. If publishing the second
    # member fails, restore the first one from the automatic rollback backup.
    $hadPackage = [IO.File]::Exists($pkgPath)
    $hadHash = [IO.File]::Exists($hashPath)
    $packagePublished = $false
    $hashPublished = $false
    try {
        if ($hadPackage) {
            [IO.File]::Replace($tempPackagePath, $pkgPath, $backupPackagePath, $true)
        }
        else {
            [IO.File]::Move($tempPackagePath, $pkgPath)
        }
        $packagePublished = $true

        if ($hadHash) {
            [IO.File]::Replace($tempHashPath, $hashPath, $backupHashPath, $true)
        }
        else {
            [IO.File]::Move($tempHashPath, $hashPath)
        }
        $hashPublished = $true
    }
    catch {
        $publishFailure = $_
        $rollbackFailures = [System.Collections.Generic.List[string]]::new()

        if ($hashPublished -or [IO.File]::Exists($backupHashPath)) {
            try {
                if ($hadHash) {
                    if (-not [IO.File]::Exists($backupHashPath)) { throw 'checksum backup is missing' }
                    if ([IO.File]::Exists($hashPath)) {
                        [IO.File]::Replace($backupHashPath, $hashPath, $null, $true)
                    }
                    else { [IO.File]::Move($backupHashPath, $hashPath) }
                }
                elseif ([IO.File]::Exists($hashPath)) { [IO.File]::Delete($hashPath) }
            }
            catch { $rollbackFailures.Add("checksum: $($_.Exception.Message)") }
        }
        if ($packagePublished -or [IO.File]::Exists($backupPackagePath)) {
            try {
                if ($hadPackage) {
                    if (-not [IO.File]::Exists($backupPackagePath)) { throw 'package backup is missing' }
                    if ([IO.File]::Exists($pkgPath)) {
                        [IO.File]::Replace($backupPackagePath, $pkgPath, $null, $true)
                    }
                    else { [IO.File]::Move($backupPackagePath, $pkgPath) }
                }
                elseif ([IO.File]::Exists($pkgPath)) { [IO.File]::Delete($pkgPath) }
            }
            catch { $rollbackFailures.Add("package: $($_.Exception.Message)") }
        }

        if ($rollbackFailures.Count -gt 0) {
            throw "Publishing failed ($($publishFailure.Exception.Message)); rollback also failed: " +
                  ($rollbackFailures -join '; ') +
                  ". Preserved rollback files: '$backupPackagePath', '$backupHashPath'."
        }
        foreach ($backup in @($backupPackagePath, $backupHashPath)) {
            if ([IO.File]::Exists($backup)) { [IO.File]::Delete($backup) }
        }
        throw $publishFailure
    }

    foreach ($backup in @($backupPackagePath, $backupHashPath)) {
        if ([IO.File]::Exists($backup)) {
            try { [IO.File]::Delete($backup) }
            catch { Write-Warning "Could not remove publish backup '$backup': $($_.Exception.Message)" }
        }
    }

    Write-Ok "$pkgPath"
    Write-Ok "$([math]::Round((Get-Item -LiteralPath $pkgPath).Length / 1KB, 1)) KB"
    Write-Ok "sha256 $pkgHash"
}
finally {
    if ($stage -and (Test-Path -LiteralPath $stage)) {
        try { Remove-Item -LiteralPath $stage -Recurse -Force }
        catch { Write-Warning "Could not remove staging directory '$stage': $($_.Exception.Message)" }
    }
    foreach ($temporaryOutput in @($tempPackagePath, $tempHashPath)) {
        if ($temporaryOutput -and [IO.File]::Exists($temporaryOutput)) {
            try { [IO.File]::Delete($temporaryOutput) }
            catch { Write-Warning "Could not remove temporary output '$temporaryOutput': $($_.Exception.Message)" }
        }
    }
}
