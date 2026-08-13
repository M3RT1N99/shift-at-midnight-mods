# Shift At Midnight — Mods

Mods for *Shift At Midnight* (Kwalee) and a small installer that keeps them up to date.

Ships no game assets and no music. Everything here is original work; packages carry only
the author's own files.

---

## MidnightRadio

The game has a placeable boombox that loops one short track. This replaces that with your
own music — **synchronised across the whole lobby**.

- Everyone hears the same track at the same position.
- **Anyone** can queue or start a track. There is no host-only DJ.
- Local audio files work with no dependencies at all.
- A URL box optionally drives [yt-dlp](https://github.com/yt-dlp/yt-dlp), which you install
  yourself. Each client resolves the URL independently, so no audio travels between players.
- Your own volume and mute stay local and never disturb the shared timeline.

All co-op players need the mod. Players without it are unaffected and hear the game's
original audio.

### How syncing works

Playback rides on the game's existing Photon Fusion connection using its reliable-data
channel, so the mod adds no netcode of its own and cannot desync the game simulation — it
only swaps an `AudioSource` clip.

The host acts as a sequencer rather than an owner: it stamps a revision on each accepted
change and relays it, so every peer applies changes in the same order while anyone can
still queue. Position is derived from Fusion's simulation tick, and drift is corrected
continuously — a gentle pitch nudge for small errors, a seek only for large ones.

Only a track identifier crosses the wire. A peer that cannot resolve a track says so and
stays silent instead of guessing at a substitute.

---

## OsamaBinLaden

A work-in-progress, single-player-only Hunt monster. It creates a stylised low-poly
figure at runtime, charges the local player while playing a generated scream, then
detonates with a local effect and configurable distance-based damage.

It contains no photographs, recordings, models, textures or extracted game assets. The
runtime refuses to spawn unless the game positively confirms solo mode and re-checks that
gate before applying damage; it never sends network data.

---

## sam-mod

A dependency-free installer and updater. SHA-256 comes from Windows CNG, HTTPS from
WinHTTP, and the zip reader and DEFLATE decompressor are in-tree — so each build is a
single self-contained `.exe` you can hand to someone who has nothing installed.

Two front ends over the same code:

- **`sam-mod-gui.exe`** — double-click it. Finds the game, lists what is installed, and
  offers Update / Install from file / Remove / Verify. This is the one to give other players.
- **`sam-mod.exe`** — the console tool, for scripting and CI.

```
sam-mod list                    show installed mods
sam-mod install <file.modpkg>   install from a local package
sam-mod update                  fetch the newest release from GitHub
sam-mod uninstall <slug>        remove a mod and restore the originals
sam-mod verify                  check installed files against recorded hashes
```

It errs toward doing nothing rather than doing damage:

- Refuses to write through a reparse point, so a symlinked mirror of a game directory can
  never be mistaken for a local copy.
- Verifies a package against its own `SHA256SUMS` before unpacking anything.
- Backs displaced files up to a per-mod vault; uninstall restores the original bytes.
- Journals every step and rolls the whole install back on any failure.
- Refuses payloads that target game binaries or escape `Mods/` and `UserData/`.
- Leaves `UserData/` alone on uninstall — your music and edited config are yours.

---

## Building

**The mod** needs [MelonLoader](https://github.com/LavaGang/MelonLoader) 0.7.3+ installed
into the game once, so it can generate the IL2CPP interop assemblies the plugin references.

```powershell
.\mods\MidnightRadio\scripts\build.ps1
.\scripts\pack.ps1 -Mod MidnightRadio       # produces dist/MidnightRadio-<version>.modpkg
.\mods\OsamaBinLaden\scripts\build.ps1 -NoDeploy
.\scripts\pack.ps1 -Mod OsamaBinLaden       # WIP package for local testing
```

**The installer** needs only a C++20 compiler — clang++ or MSVC, both from Visual Studio.

```powershell
.\tools\sam-mod\build.ps1
```

Tests:

```powershell
dotnet run --project mods\MidnightRadio\tests\MidnightRadio.SmokeTests.csproj
dotnet run --project mods\OsamaBinLaden\tests\OsamaBinLaden.SmokeTests.csproj
```

---

## Target

| | |
|---|---|
| Engine | Unity 6000.0.69f1, IL2CPP, URP, x64 |
| Netcode | Photon Fusion (Host mode, up to 3 players) + Photon Voice |
| Loader | MelonLoader 0.7.3+ |

Why MelonLoader and not BepInEx: MelonLoader has closed bugs specifically on the 6000.0
branch and ships a newer Cpp2IL, while BepInEx's 6000.0 crash report has been open since
March 2025. The loader-specific code is confined to two files, so switching is cheap if
that changes.

---

## Status

MidnightRadio is not released yet. The playback, library, UI, sync and installer paths are
written and tested, but the Fusion receive hook has only been verified against game
metadata — not yet in a running game. Until it is, the mod deliberately stays in local
playback rather than pretending to be synced.

## Legal

Mod packages contain only their author's own files — never game assets, never music.

yt-dlp is separate open-source software you install yourself; it is neither bundled nor
downloaded automatically. Fetching content conflicts with some platforms' terms of service,
and what you download is your responsibility.
