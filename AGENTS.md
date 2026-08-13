# Working in this repository

Guidance for anyone — human or agent — building mods for *Shift At Midnight*.

Read the "Hard rules" and "Verified target facts" sections before writing code. The facts
below were established by reading the shipped binaries; re-deriving them wastes hours.

---

## Hard rules

These are not style preferences. Breaking one produces a package that cannot be
distributed, or damages someone's game installation.

1. **Never bundle game content.** No audio, textures, meshes, scenes, `.assets`,
   `.resource`, `.bundle`, or game binaries. A package carries only its author's own files.
   `scripts/pack.ps1` enforces this with an exact inventory plus magic-byte sniffing, so a
   renamed file will not slip through.
2. **Never bundle third-party tools.** yt-dlp and ffmpeg are installed by the user. Do not
   ship them and do not download them silently.
3. **`Shift At Midnight/` in the working copy is a symlink mirror of the Steam install.**
   Writing through it writes the real game files. Never write there. `sam-mod` refuses to
   follow reparse points; keep it that way.
4. **Nothing may throw into a game callback.** Update loops, scene hooks and network
   callbacks must be wrapped (`Log.Guard`). A mod bug should degrade the mod, not end the
   session.
5. **Never break unmodded players.** A player without the mod must be unaffected. Verify
   this claim for anything touching the network.
6. **Fail closed on the network.** The binary contains
   `"Disconnecting client for sending reliable data when not allowed"` — an unpermitted
   send does not throw, it disconnects the player. Gate every send on a runtime check.

---

## Layout

```
mods/<ModName>/          one mod
  mod.json               manifest (schema 1)
  README.md CHANGELOG.md LICENSE THIRD-PARTY-NOTICES.txt    all required by the packer
  src/                   C# sources + .csproj
  payload/               exactly what gets installed into the game
    Mods/<ModName>/      plugin assembly + loader manifest
    UserData/<ModName>/  seed files: config template, folders for the player's own data
  scripts/build.ps1      compile -> build/<Config>/<ModName>.dll
  tests/                 smoke tests with Unity/loader stubs
scripts/pack.ps1         mod folder -> dist/<slug>-<version>.modpkg
tools/sam-mod/           C++ installer and updater
```

`payload/` mirrors the game directory. Only `Mods/` and `UserData/` are allowed roots — the
installer rejects anything else.

---

## Verified target facts

| | |
|---|---|
| Engine | Unity **6000.0.69f1**, IL2CPP, URP, x64 |
| Metadata | `global-metadata.dat` **unencrypted**, magic `0xFAB11BAF`, version **31** |
| Build GUID | `8e59f2b32a5f4d15901aa64b66c56dcf` · Steam AppID `3722330` |
| Netcode | Photon Fusion, **`GameMode.Host`**, max **3** players, + Photon Voice |
| Loader | MelonLoader 0.7.3+ |
| Anti-cheat | none |

**Loader choice.** MelonLoader, not BepInEx: it has closed bugs specifically on the 6000.0
branch and ships a newer Cpp2IL, while BepInEx's 6000.0 crash (#1079) has been open since
March 2025. The Unity 6 breakages you will read about online are metadata **v39**
(Unity 6000.3.x) — this game is v31, on the supported side of that line.

**Dumping.** Il2CppDumper does **not** support metadata v31. Use Cpp2IL or
Il2CppInspectorRedux. For writing mods you usually need neither: MelonLoader generates
referenceable assemblies into `MelonLoader\Il2CppAssemblies\` on first launch. Game types in
the global namespace appear under the `Il2Cpp` namespace prefix.

**Authority model.** The game names its RPCs by direction, which hands you the permission
model for free:

- `Rpc_CMD_<X>` (176 of them) — client → host request
- `Rpc_<X>` (293) — host → all broadcast

They pair up almost 1:1. If you need to change networked state, find the `Rpc_CMD_` entry
point rather than writing the state directly.

**Networking from a mod.** Fusion's reliable-data channel carries arbitrary payloads over
the existing connection: `SendReliableDataToServer(key, data)`,
`SendReliableDataToPlayer(player, key, data)`, received via
`FusionCallbackBase.OnReliableDataReceived(...)` — a plain `public virtual` method, so a
Harmony patch reaches it. Do **not** try to implement `INetworkRunnerCallbacks` from a
plugin; that means injecting a managed type implementing an Il2Cpp interface, which is the
fragile path. See `mods/MidnightRadio/src/Sync/` for a working example.

---

## Creating a new mod

1. Copy `mods/MidnightRadio/` as a starting point. Keep the four required doc files — the
   packer refuses to build without them.
2. Edit `mod.json`. Required fields: `schema` (1), `id` (reverse-DNS), `slug` (a simple
   folder-safe name, must match the folder and the DLL), `name`, `version` (canonical
   semver), `payload`, `capabilities`.
3. Declare every payload entry. The packer verifies each `src` resolves inside the package;
   the installer maps `payload/<root>/<rest>` onto `<game>/<root>/<rest>`.
4. Declare bundled libraries explicitly in `bundledLibraries` with `path` and `licenseFile`.
   Any undeclared `.dll` under `payload/Mods/` fails the content guard — this is deliberate.
5. Write the plugin. Keep loader-specific code in one file (see `Log.cs` and `Main.cs`); the
   rest should be plain Unity and C# so a loader change stays cheap.
6. Add smoke tests. `tests/PluginCompileSmoke/` stubs Unity and the loader so the whole
   plugin compiles and its logic can be tested without the game.

### Commands

```powershell
.\mods\<ModName>\scripts\build.ps1                 # compile
.\scripts\pack.ps1 -Mod <ModName>                  # -> dist/<slug>-<version>.modpkg
dotnet run --project mods\<ModName>\tests\*.SmokeTests.csproj
.\tools\sam-mod\build.ps1                          # build the installer
.\tools\sam-mod\build\sam-mod.exe install <pkg> --game <dir>
```

Test installs against a scratch directory containing a dummy `ShiftAtMidnight.exe`, never
against the real install.

---

## Known traps

**The boombox volume is animated.** The in-game "Toggle Music" interaction does not play or
stop anything: the `AudioSource` on `Boombox Placed > Music Audio` loops forever and an
Animator animates `m_Volume` between 0 and 0.15. Anything you write to `.volume` is
overwritten every frame. Use `mute` instead — it is not animated. The animated volume is
also a free, already-replicated on/off signal, because the interaction runs through
`Rpc_CMD_Interact → Rpc_Interact`.

**Audio mixer routing.** Copy `originalSource.outputAudioMixerGroup` rather than looking the
group up by name, so the player's music slider keeps working across game updates.

**MP3 is the risky format.** Unity decodes Ogg Vorbis and WAV reliably on Windows
standalone; MP3 is historically restricted. Report a clear "convert this file" error rather
than failing silently.

**`PCMReaderCallback` is a trap under Il2CppInterop.** It needs a managed delegate to
survive marshalling into the native audio thread. Feed a `file://` URL to
`UnityWebRequestMultimedia` instead and let native code do the decoding.

**The text databases are encrypted.** The `StreamingAssets` JSON files are Base64 of an
encrypted blob and are read through `SimpleJsonCrypto` / `JSONAccess`. A local tool handles
decrypt/re-encrypt; it is intentionally **not** in this repository and must not be added.
Note `JSONAccess.ShouldSkipDecryption` — the loader appears to accept plaintext JSON
directly, which would let a mod ship readable files with no crypto at all. Worth testing.

**Fusion prefabs are baked.** New networked prefabs must be registered in the
`NetworkProjectConfig` at build time, so a mod cannot `Runner.Spawn` a brand-new object.
For custom entities, spawn locally on every modded client and synchronise state over the
reliable-data channel instead.

---

## Game systems reference

Established from metadata; use it instead of re-reconning.

**Enemies.** `Enemy : Fusion.NetworkBehaviour` is a thin base with three virtuals
(`Leave`, `CheckIfNearBarricade`, `ChaseNonPlayerTarget`). Subclasses: `Spider` (the richest
template), `BabyDoll` (telegraph-then-leap), `WeepingAngel` (look-gated), `Marionette`.
`HuntManager` is the singleton driving hunts.

**Damage.** `Hittable : Fusion.NetworkBehaviour` (68 fields / 46 methods) is the universal
health, damage and death component. Route damage through it so it replicates.

**NPCs.** `StoreManager` (200/141) is the runtime spawner (`SpawnBrowsingNPC`,
`SpawnNuisanceNPC`, `npcSpawnPoints`). `Npc` is a **ScriptableObject** spawn-table entry,
not a behaviour. `EndlessGenerationManager.GenerateNight()` builds each night's roster;
`CurrentDayManager` consumes it.

**Audio.** `AmbientMusicSystem` (`ambientTracks`, `curTrackIndex`, `inHunt`,
`pauseAmbience`), `PlayAudioArray`, `MainMenuMusic`, `AudioVolumeSliders`
(exposed mixer parameters `MusicVolume` and `SFXVolume`).

**Useful log.** The game writes `%USERPROFILE%\AppData\LocalLow\Kwalee\ShiftAtMidnight\Player.log`
with `[FUSION]` lines showing game mode, player joins and shutdown reasons. Check it first
when debugging anything networked.

---

## Reverse engineering

Metadata is unencrypted, so class, method and field names are all readable. IL2CPP stores
managed string literals in `global-metadata.dat`, **not** in `GameAssembly.dll` — running
`strings` on the DLL and finding nothing means very little.

The build also retained the original source file paths of all 1,239 game scripts
(`\Assets\Scripts\...`), which makes the codebase easy to map.

Tooling for this lives outside the repository by design and is gitignored. Keep it that
way: this repository distributes mods, not analysis of the game.
