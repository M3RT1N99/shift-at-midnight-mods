# OsamaBinLaden development handoff

## Current state

The functional MVP is implemented. It observes the rising edge of
`HuntManager.huntInProgress`, resolves the local `PlayerManager`, creates a runtime-only
primitive character, charges directly toward the player, plays original generated PCM,
then shows a local primitive blast and applies bounded distance-based damage.

There are deliberately no Harmony patches, Fusion spawns, RPCs, reliable-data messages,
asset bundles, media files or extracted game objects. The direct chase is intentionally a
first slice and does not yet path around walls.

## Non-negotiable network gate

The mod is single-player-only. Before creating an NPC or modifying damage state, the code
must positively establish that no Fusion multiplayer session is active. An unknown or
partially initialised runner state counts as multiplayer and therefore disables the mod.

- Never call `Runner.Spawn` for this entity; Fusion prefabs are baked.
- Never send a Fusion RPC or reliable-data payload.
- Never modify a replicated object in multiplayer.
- Re-check the gate when scenes and runner state change.
- If multiplayer becomes active, immediately stop the behaviour and remove every local
  object owned by the mod.
- Wrap loader, Unity, scene and gameplay callbacks so no exception escapes into the game.

## Source boundaries

- `Main.cs`: MelonLoader attributes and lifecycle only.
- `Log.cs`: guarded callback helpers.
- `SessionGate.cs`: fail-closed single-player detection.
- `ModController.cs`: Hunt edge, spawn selection, damage boundary and cleanup state.
- `RuntimeCharacter.cs`: primitives, pursuit, generated sound, fuse and local blast.
- `SessionGate.cs`: positive solo-mode and local-player proof.
- `ExplosionMath.cs`: bounded distance falloff independent from Unity.
- `Config.cs`: bounded configuration loading and validation.

## Verification gates before packaging

- Keep the existing pure smoke tests green for malformed configuration, bounds, locked
  safety policy, atomic persistence and damage falloff.
- Add automated narrow stubs for unknown/host/client session transitions if the controller
  gains more state or networking APIs.
- Verify repeated scene loads and shutdown in game for leaked objects or audio clips.
- Build Debug and Release against the generated IL2CPP assemblies with no warnings.
- Verify in game that a real Hunt spawns one figure, pursuit and generated audio work,
  `PlayerManager.TakeDamage` executes once, and Hunt/scene cleanup is reliable.
- Verify that entering or joining any non-solo session immediately leaves no owned object.
- Test installation only against a scratch game directory with a dummy executable.
- Run the repository content guard and confirm the package contains no assets or media.

Until the in-game gates pass, keep the manifest and release notes marked work-in-progress.
