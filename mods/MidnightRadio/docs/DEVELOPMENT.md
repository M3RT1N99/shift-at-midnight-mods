# MidnightRadio development handoff

## Current state

Implemented and covered by local smoke tests:

- MelonLoader lifecycle entry point
- placed-boombox discovery and vanilla-loop suppression
- local audio loading and playback controls
- F4 IMGUI panel
- recursive music library with cached SHA-256 identity, deduplication and atomic cache
- optional user-installed yt-dlp/ffmpeg discovery and opt-in download path
- bounded URL cache, track-duration limit, shuffle and repeat modes
- versioned wire protocol and drift-correction helpers
- fail-closed Fusion runner adapter

Not enabled yet:

- Fusion reliable-data receive hook
- handshake, peer roster, state snapshot, host relay and late-join session state
- synchronized multiplayer playback

The network adapter cannot transmit until the receive path explicitly calls
`RunnerBridge.MarkReceiveReady(true)`. This is intentional: Fusion can disconnect a player
who sends reliable data in a mode where it is not permitted.

The reflection bridge is scaffolding, not a complete Fusion binding. The generated Fusion
2.0.11 wrapper was inspected for game build 24450017. Its exact public send projections are:

- `NetworkRunner.SendReliableDataToServer(ReliableKey, Il2CppStructArray<byte>)`
- `NetworkRunner.SendReliableDataToPlayer(PlayerRef, ReliableKey, Il2CppStructArray<byte>)`

The corresponding receive callback is
`INetworkRunnerCallbacks.OnReliableDataReceived(NetworkRunner, PlayerRef, ReliableKey,
Il2CppSystem.ArraySegment<byte>)`. The receive segment is an IL2CPP value wrapper, not a
managed `System.ArraySegment<byte>`, and must be copied before the callback returns.

## Verified runtime baseline

On game build `24450017` with MelonLoader x64 `0.7.3.0`:

- IL2CPP assembly generation completed successfully.
- Debug and Release builds completed with zero warnings and zero errors.
- MelonLoader reported `1 Mod loaded` and MidnightRadio reached its ready state.
- The initial empty-library scan and a clean game shutdown completed without a mod error.

Still requiring an interactive gameplay check: opening/using the F4 panel, loading a real
audio file, adopting a placed boombox, playback controls, scene transitions and the optional
URL path.

## Required next network step

1. Select and verify one concrete game receive method (`FusionNetworkManager` or
   `FusionCallbackBase`) with the callback signature above. Refuse to attach when the game
   build or method identity differs from the verified target.
2. Copy `Il2CppSystem.ArraySegment<byte>` immediately into a managed byte array, validate
   the reliable key, then forward the managed segment to `SyncTransport.Dispatch`.
3. Only after a local 200-frame loopback succeeds, call `MarkReceiveReady(true)` and build
   the handshake/session layer. Never broadcast before the confirmed-peer roster exists.

## Local verification

```powershell
dotnet run --project ./mods/MidnightRadio/tests/MidnightRadio.SmokeTests.csproj
dotnet build ./mods/MidnightRadio/tests/PluginCompileSmoke/PluginCompileSmoke.csproj
```

The first command tests protocol parsing, malformed frames, UTF-8 caps, config persistence,
and the hash-indexed library. The second compiles the complete production source tree
against narrow loader/Unity stubs to catch ordinary C# errors while the generated interop
assemblies are unavailable.

The library reuses a cached SHA-256 when both file size and last-write time are unchanged.
Use **Neu laden** after replacing files, and preserve accurate timestamps when external
tools manage the music folder.

## Packaging

The package under `dist/` now contains the real Release DLL and passed the packer's exact
inventory, internal `SHA256SUMS`, archive round-trip and sidecar-hash checks. The packer
rejects packages without `payload/Mods/MidnightRadio/MidnightRadio.dll` and verifies every
declared bundled library plus its licence file. Treat this as a development candidate until
the interactive playback checks above pass.
