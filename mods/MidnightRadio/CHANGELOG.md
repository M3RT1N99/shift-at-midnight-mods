# Changelog

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
