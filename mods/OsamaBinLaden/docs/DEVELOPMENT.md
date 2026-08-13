# OsamaBinLaden development handoff

## Current state

This directory currently contains scaffold and policy only. There is no production C#
source, gameplay implementation, smoke-test project, built DLL or release package.

The intended local sequence is:

1. Identify a safe single-player gameplay state and a stable attack trigger.
2. Create a local-only NPC using runtime components; do not register or spawn a new Fusion
   prefab.
3. On attack, select the local player, enter a charge state and use NavMesh/Astar only
   after a runtime capability check.
4. Play a scream through a local `AudioSource` without bundling audio. Any reused clip
   must already be loaded by the game; any custom file must remain user-owned UserData.
5. At the configured distance, show a local explosion effect and route damage through the
   game's `Hittable` component only after its single-player call path is verified.
6. Destroy the spawned runtime object and clean up every event hook on scene change and
   mod shutdown.

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

## Suggested source boundaries

- `Main.cs`: MelonLoader attributes and lifecycle only.
- `Log.cs`: guarded callback helpers.
- `SessionGate.cs`: fail-closed single-player detection.
- `NpcController.cs`: explicit idle, charge, detonate and cleanup states.
- `NpcFactory.cs`: local runtime object/component construction.
- `TargetResolver.cs`: local-player resolution without peer targeting.
- `Effects.cs`: local audio, particles and screen effects with graceful fallbacks.
- `Config.cs`: bounded configuration loading and validation.

## Verification gates before packaging

- Add a compile-smoke project with narrow MelonLoader/Unity/game stubs.
- Test malformed configuration, invalid numeric ranges and atomic config recovery.
- Test unknown runner state, host/client state and a mid-scene transition to multiplayer;
  all must leave no NPC and send no network traffic.
- Test repeated scene loads and shutdown for leaked objects, delegates and coroutines.
- Build Debug and Release against the generated IL2CPP assemblies with no warnings.
- Verify in game that the attack targets only the local player, damage uses `Hittable`, and
  cleanup is reliable.
- Test installation only against a scratch game directory with a dummy executable.
- Run the repository content guard and confirm the package contains no assets or media.

Until every gate passes, do not deploy to the real installation and do not package a
release.
