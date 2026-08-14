# OsamaBinLaden development handoff

## Current state

The functional MVP is implemented for both solo and multiplayer. In solo, it observes the
rising edge of `HuntManager.huntInProgress`, resolves the local `PlayerManager`, creates a
runtime-only primitive character, charges directly toward the player, plays original
generated PCM, then shows a local primitive blast and applies bounded distance-based
damage.

In multiplayer, the Fusion **host** runs the same encounter logic against one eligible
target (itself or a validated peer) and broadcasts what happened over a small custom
reliable-data protocol; every other validated peer only ever renders a cosmetic mirror of
the host's character and never applies damage itself. Players who never complete the
handshake (no mod, or the mod disabled) are never selected as a target and never receive
a message.

There are deliberately no Harmony patches for gameplay, no Fusion object spawns, no
asset bundles, media files or extracted game objects. The one Harmony patch that does
exist (`Multiplayer/ReceiveHook.cs`) only forwards this mod's own reliable-data key into
its transport; it changes nothing about how the game's own networking behaves. The direct
chase is intentionally a first slice and does not yet path around walls, in solo or in
multiplayer.

## Non-negotiable network gate

The mod must positively establish live session state before creating an NPC or modifying
damage state - in solo *and* in multiplayer. An unknown or partially initialised runner
state counts as inactive and disables the mod, exactly the same way in both modes.

- Solo: `SessionGate.TryGetSoloPlayer` must succeed before anything is created; the target
  in a detonation callback is re-resolved and re-proven, never trusted as-is.
- Multiplayer: `EncounterSession.IsActive` (a live, positively-confirmed non-solo Fusion
  runner) gates everything; `SpawnPlacement`/pursuit code never runs without it.
- Only the host ever calls `PlayerManager.TakeDamage`, and only after re-resolving the
  target's identity against current Fusion state
  (`FusionTransport.RevalidateHostDamageTarget`) at the moment of detonation - the same
  "re-check at the irreversible boundary" pattern solo mode already used.
- The host only ever sends `Spawn`/`Detonate`/`Cancel`/`Heartbeat` to peers that completed
  a full three-way handshake (`Hello` → `HelloAck` → `Ready`) with matching nonces and the
  host's current epoch; a peer that never speaks the protocol - i.e. never installed the
  mod - is never contacted and never targeted.
- Every peer-facing send goes through `EncounterProtocol.TryEncode`, which refuses to
  serialise anything outside the same numeric bounds `Config.Clamp` already enforces, and
  every receive goes through `TryDecode`, which independently re-validates those bounds
  and the message shape before the payload is trusted.
- Re-check the gate every scene load, session-state change and every tick a target is
  being pursued - not just once at spawn.
- If the session becomes ambiguous, or the current target disconnects, immediately stop
  the behaviour and remove every local object owned by the mod (`Cancel` is sent to peers
  first, when there is time to).
- Wrap loader, Unity, scene and gameplay callbacks so no exception escapes into the game.
- `singlePlayerOnly` / `safety.disableInMultiplayer` remain an explicit, user-facing
  opt-out back to the original single-player-only behaviour; `safety.allowNetworkSends` is
  a second, independent switch that - if false - lets the mod detect multiplayer without
  ever calling a `FusionTransport` send method.

## Source boundaries

- `Main.cs`: MelonLoader attributes and lifecycle only.
- `Log.cs`: guarded callback helpers.
- `SessionGate.cs`: fail-closed single-player detection.
- `ModController.cs`: Hunt edge (solo), role dispatch (solo vs. multiplayer), cleanup
  state, and the solo damage boundary.
- `SpawnPlacement.cs`: shared farthest-eligible-spawn-point selection, used by solo and by
  the multiplayer host.
- `RuntimeCharacter.cs`: primitives, pursuit, generated sound, fuse, local blast, and the
  `DetonationCause` a caller can use to log or report why a character detonated.
- `ExplosionMath.cs`: bounded distance falloff independent from Unity.
- `Config.cs`: bounded configuration loading and validation, solo and multiplayer.
- `Multiplayer/EncounterProtocol.cs`: the versioned, bounded, little-endian wire format.
  No Unity, MelonLoader or Fusion dependency, so malformed input is tested in isolation.
- `Multiplayer/SequenceGuard.cs`: pure per-sender replay/freshness check (epoch + strictly
  increasing sequence), independently smoke-tested.
- `Multiplayer/FusionTransport.cs`: narrow adapter over the game's existing Fusion runner;
  owns the confirmed-peer set, host/solo detection, and every actual send/receive call.
- `Multiplayer/ReceiveHook.cs`: the one Harmony patch, forwarding only this mod's reliable
  -data key into `FusionTransport`.
- `Multiplayer/EncounterSession.cs`: the host and client state machines - handshake,
  target selection, encounter lifecycle, and the host-only damage boundary.

## Verification gates before packaging

- Keep the existing pure smoke tests green for malformed configuration, bounds, the
  single-player-only mirroring, atomic persistence, damage falloff, protocol encode/decode
  (including truncation, header and cap rejection, and the three-way handshake shapes),
  and `SequenceGuard` replay/epoch behaviour.
- Add automated narrow stubs for any additional host/client session transition if the
  encounter session gains more state or networking APIs.
- Verify repeated scene loads and shutdown in game for leaked objects or audio clips, in
  solo and in a multiplayer session.
- Build Debug and Release against the generated IL2CPP assemblies with no warnings.
- Verify in solo that a real Hunt spawns one figure, pursuit and generated audio work,
  `PlayerManager.TakeDamage` executes once, and Hunt/scene cleanup is reliable.
- Verify in a real multiplayer session, with two modded clients and the host:
  - The host's Hunt spawns exactly one figure, targeting either itself or a peer.
  - Every validated peer sees a synced pursuit, hears the scream, and sees the blast at
    the same time the host detonates, even under moderate latency.
  - Only the host's local damage call fires, and it hits the correct player.
  - An unmodded third client (or the mod disabled on one client) never sees the character
    and is never targeted.
  - Disconnecting the current target cancels the encounter cleanly on every peer.
  - Disconnecting the host resets every remaining client's handshake without an exception.
- Verify that entering or joining any session whose state cannot be positively classified
  leaves no owned object, in both roles.
- Test installation only against a scratch game directory with a dummy executable.
- Run the repository content guard and confirm the package contains no assets or media.

Until the in-game gates pass, keep the manifest and release notes marked work-in-progress.
