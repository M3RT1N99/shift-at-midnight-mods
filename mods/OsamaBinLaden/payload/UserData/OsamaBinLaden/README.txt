OsamaBinLaden user configuration
================================

On first installation, config.json.default is copied to config.json without
overwriting an existing configuration.

The plugin generates its low-poly character, scream and explosion at runtime.
The package contains no audio, model, texture, game asset or other media. Do not
place game files or third-party tools in this directory.

The mod works in both single-player and multiplayer. In multiplayer, only the
Fusion host ever decides the encounter and applies damage; every other player
who also has the mod installed only ever sees a synced, cosmetic copy. A
player without the mod is never targeted and never sees anything - unmodded
players are unaffected by design.

singlePlayerOnly and safety.disableInMultiplayer are two names for the same
switch: set either one to true to force the mod back to single-player-only
behaviour, even when a live multiplayer session is detected. The plugin keeps
both in sync in the saved file. safety.allowNetworkSends is the actual
permission gate checked before any network message is sent; set it to false
to let the mod detect multiplayer without ever putting a byte on the wire.
