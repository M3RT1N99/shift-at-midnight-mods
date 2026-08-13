# Osama Bin Laden NPC

Planned *Shift At Midnight* mod for a fictionalised single-player monster NPC. When its
attack begins, it should run toward the local player, play a scream and detonate at close
range.

> **Status:** scaffold only. There is no gameplay code or distributable DLL yet.

## Safety boundary

This mod is deliberately single-player-only. The implementation must check the active
Fusion session before it creates any object, disable itself when multiplayer is active,
and remove its local runtime objects if the session changes. It must never send an RPC or
reliable-data message.

No portrait, model, texture, audio clip, game asset or third-party executable is included.
A later implementation may reuse an already loaded game effect at runtime or let the user
select their own local sound, but that material must not enter the package.

## Development

Install MelonLoader 0.7.3+ into the real Steam installation and launch the game once so
`MelonLoader/Il2CppAssemblies` is generated. After production source files exist, compile
without deploying with:

```powershell
./mods/OsamaBinLaden/scripts/build.ps1 -Configuration Debug -NoDeploy
```

Do not package or deploy this scaffold. See `docs/DEVELOPMENT.md` for the implementation
gates that must be completed first.
