# Changelog

## 0.2.0 - 2026-08-15

- Added multiplayer support. A Fusion **host** simulates the Hunt encounter and is the
  only machine that ever calls `PlayerManager.TakeDamage`; every other player only ever
  renders a cosmetic mirror driven by the host's messages.
- Added a versioned, bounded, little-endian encounter protocol
  (`Multiplayer/EncounterProtocol.cs`) and a narrow Fusion transport adapter
  (`Multiplayer/FusionTransport.cs`, `Multiplayer/ReceiveHook.cs`) - both already present
  as unwired scaffolding - plus the missing session layer
  (`Multiplayer/EncounterSession.cs`) that actually runs the handshake and the encounter
  over them.
- Added a three-way handshake (Hello/HelloAck/Ready) with per-peer nonces and a host
  epoch, and a pure `SequenceGuard` replay/freshness check on the client's view of the
  host's messages. None of this is authentication by itself - Fusion's own sender
  identity is - it rejects stale and duplicated frames.
- Eligible encounter targets are limited to the host and players who complete the
  handshake; a player without the mod, or with it disabled, is never targeted and never
  receives a message, so unmodded players are unaffected.
- Extracted the farthest-eligible-spawn-point selection into `SpawnPlacement.cs`, shared
  by solo and multiplayer, and added a `DetonationCause` to `RuntimeCharacter`'s
  detonation callback so a multiplayer host can report why a character detonated.
- Turned `singlePlayerOnly`, `safety.disableInMultiplayer` and `safety.allowNetworkSends`
  from locked-always-on/off invariants into real, mirrored, user-facing switches, and
  added a bounded `multiplayer` config section for handshake, heartbeat, peer-timeout and
  lobby-marker timing. Bumped the config schema to version 2.
- Extended the pure logic smoke tests to 19: config single-player-only mirroring, and
  `SequenceGuard` acceptance/replay/epoch behaviour.
- Still no character assets, recordings, images, game assets or other media are included.

## 0.1.0 - 2026-08-13

- Added the initial manifest, configuration seed, development notes, project file and
  atomic build/deploy script.
- Declared a strict single-player-only and fail-closed networking boundary.
- Added Hunt-edge detection, local-player targeting, runtime primitive character and
  direct charging behaviour.
- Added a generated spatial scream, close-range fuse, local explosion effect and bounded
  damage falloff with a second solo-mode check at the damage boundary.
- Added configuration persistence and eight package-free smoke tests.
- No character assets, recordings, images, game assets or other media are included.
