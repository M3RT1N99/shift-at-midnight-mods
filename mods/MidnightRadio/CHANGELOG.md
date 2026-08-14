# Changelog

## 1.2.3 - 2026-08-15

- Synchronised playback is on by default again, and existing configs are migrated to it.
  It shipped off in 1.2.0 while the Fusion receive hook was unproven, but both players
  install the same package - so both got it disabled, and "everyone hears the same music"
  could not happen for anyone who did not hand-edit a file. The hook is now applied only
  once a session is running, never during load, and setting `sync.enabled` back to false
  returns playback to local.

  Both players still need the mod, and each needs the track itself: only an identifier
  crosses the wire, never audio.

## 1.2.2 - 2026-08-15

- The manager starts the game through Steam instead of running the executable directly.
  Launching the exe was copied from a 7 Days To Die updater, where bypassing the launcher
  is deliberate; here it left the game without Steam's context, so SteamAPI_Init() failed,
  the game's own PlatformManager threw in a loop, and it sat on the splash screen. That
  looked exactly like a mod hang and was chased as one twice. MelonLoader is unaffected
  either way, since the version.dll proxy loads regardless of who started the process.

## 1.2.1 - 2026-08-15

- Fixed the splash-screen hang. ResolveRadio ran every two seconds from the first frame,
  and its fallback enumerates every GameObject in the scene - the most expensive possible
  scan, repeated while the scene was still loading and while no boombox could exist yet.
  It now backs off to a 20-second ceiling and resets once a radio is adopted. Confirmed by
  Unity's own log growing past the point it had stopped at on every hang.
- Update() logs a heartbeat with uptime and frame count, loud for the first minute. A
  stalled game can now be told apart from a stalled mod without guessing.
- The manager shows its version in the window title, and deletes the settings file older
  builds left beside it.

## 1.2.0 - 2026-08-14

The mod, the manager and the release tag now share one version number. They had drifted
apart, and the self-update could never fire because it compared two different scales.

### Fixed
- The game hung on the splash screen. The tool-update check was started from mod init as
  "fire and forget", but an async method runs synchronously up to its first await - and
  this one launches yt-dlp.exe, about a second, on the thread loading the game. It now runs
  on its own thread, well after load.
- The same probe redirected stderr without draining it. Once the pipe buffer fills the
  child blocks on write and the wait never returns; yt-dlp writes there routinely.

### Changed
- The manager finds the game through Steam's registry entry and libraryfolders.vdf instead
  of guessing paths, so unusual library locations just work. The settings file it used to
  write is gone with it - it only ever remembered a hand-typed path because the guesses
  missed.
- The console front end is no longer built. The window is what gets handed to a player, and
  maintaining two front ends for one audience earned nothing.
- The manager installs MelonLoader itself when it is missing, and can turn every mod off at
  once by parking the loader.
- Dark theme, with hover and pressed states drawn by hand.

## 1.1.1 - 2026-08-13

- Downloaded tracks are named after the video title instead of its id. The playlist
  showed entries like "Youtube-5zvn60-E1HA", which told the player nothing.
  `--restrict-filenames` is dropped with it: it strips non-ASCII and turns spaces into
  underscores, mangling exactly the titles this is meant to surface.
- Deno is provisioned alongside yt-dlp and ffmpeg. yt-dlp warns that YouTube extraction
  without a JavaScript runtime is deprecated and drops formats; that is a warning now and
  a breakage later. Treated as a degradation, not a requirement - if it cannot be
  fetched, downloads still work with fewer formats.

## 1.1.0 - 2026-08-13

First build with working playback. Verified in-game against build 24450017 with
MelonLoader 0.7.3: 120 tracks indexed, clips created, a URL fetched end to end, and no
exceptions in the loader log.

### Playback now works
- Audio is decoded out-of-process by ffmpeg into raw 32-bit float PCM and pushed into an
  AudioClip with `SetData`. Unity's own loaders cannot be used on this build:
  `DownloadHandlerAudioClip`'s `(string, AudioType)` constructor and `AudioClip.Create`'s
  plain overload are both stripped. The surviving `Create` overload is called with a **null**
  PCMReaderCallback, so no managed delegate is marshalled into the native audio thread.
- Files are read in chunks across frames; a four-minute stereo track is ~84 MB of float data.
- MP3 libraries work. Every format goes through the decoder, because nothing is natively
  loadable here.

### Tools install themselves
- yt-dlp and ffmpeg are downloaded on first use into `UserData/MidnightRadio/Tools` instead
  of being expected from the user. Downloads are written to a `.part` file and renamed only
  on success.
- yt-dlp is version-checked on every start, because it breaks within weeks as sites change.
  ffmpeg is refreshed every 30 days. Both are configurable; being offline is not an error.

### The radio, not a hotkey
- Interacting with the placed boombox opens the panel. The patch targets the local
  `Interactable.Interact`, so it neither toggles the game's music nor does anything on other
  players' machines. F4 remains as a fallback.
- The interaction prompt is relabelled from "Toggle Music" to "Radio".
- No radio placed means nothing happens: it is a purchasable decor item, so there is nothing
  to hook into until you buy one.

### UI
- The panel centres on open and is sized as a share of the screen. Scaling uses the GUI
  matrix, so layout and glyphs grow together rather than the box growing around fixed-size text.
- Player movement and look are suspended while the panel is open, and restored exactly.
- Most of `GUILayout` is stripped from this build. Each risky widget is tried once, the
  failure is logged by name, and a fallback built only from `Label`/`Button` is used from then
  on - so a stripped method costs one log line instead of an exception every frame.
- The track list pages instead of scrolling; `BeginScrollView` does not exist here.

### Fixes
- Playback was silent even when loading correctly: volume was gated on the game Animator's
  state, which nothing could set once interaction opened the panel instead of toggling music.
- Config migrations were computed and then discarded unless the game shut down cleanly.

### Multiplayer
Synchronised playback is implemented - transport, clock, session and open-queue authority -
but stays **off by default** in this release. Applying the Fusion receive hook during mod
init prevented the game from reaching a scene; it is now deferred until a session is
actually running, and that path has not yet been confirmed in live co-op. Enable
`sync.enabled` in `config.json` to test it.

## 1.0.0 - 2026-08-13
- Work in progress: local radio playback, F4 UI, hash-indexed music library, and optional
  user-installed yt-dlp/ffmpeg integration implemented.
- Multiplayer sync remains gated off pending an in-game Fusion receive-path safety test.
- Verified against game build 24450017 and MelonLoader 0.7.3: real IL2CPP Debug/Release
  builds, loader initialization, empty-library scan, clean shutdown and package integrity.
- Interactive boombox playback remains a required release-candidate gameplay check.
