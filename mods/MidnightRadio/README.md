# Midnight Radio

Play your own music locally through the in-game boombox.

> **Development status:** local playback, the F4 panel, content-hash library index and the
> opt-in yt-dlp bridge are implemented. Multiplayer transport is currently disabled until
> the Fusion receive hook has passed its in-game safety probe. The package in `dist/`
> contains the real Release DLL and has passed archive and integrity verification. Loader
> startup is verified; interactive boombox playback still needs a gameplay check.

The current development build indexes local audio by a cached SHA-256 content hash and controls playback
through an F4 panel. After an explicit one-time notice, it can also use your own
[yt-dlp](https://github.com/yt-dlp/yt-dlp) and ffmpeg installations to fetch a URL. Nothing
is installed automatically, and no track identifiers or audio data are sent to peers.

Lobby-wide playback, an open queue and late-join synchronisation are planned, but are not
active yet. Multiplayer currently remains local-only: each player hears only their own
MidnightRadio selection, and players without the mod are unaffected.

Ships no music and no game assets.

## Requirements

- MelonLoader 0.7.3+ (x64)
- Optional, for URL tracks: yt-dlp and ffmpeg, installed by you — neither is bundled
  nor downloaded automatically

For development, install MelonLoader into the real Steam game directory and launch the
game once so it generates `MelonLoader/Il2CppAssemblies`. Then run:

```powershell
./mods/MidnightRadio/scripts/build.ps1 -Configuration Debug -NoDeploy
```

## Notes

yt-dlp is open-source software with broad platform support. Fetching content conflicts
with some platforms' terms of service, and what you download is your responsibility —
prefer material you own or that is licensed for reuse.

Direct audio transfer between players is not implemented. The reserved configuration
switch remains disabled and has no effect in this development build.
