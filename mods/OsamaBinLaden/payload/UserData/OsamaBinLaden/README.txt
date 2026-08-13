OsamaBinLaden user configuration
================================

On first installation, config.json.default is copied to config.json without
overwriting an existing configuration.

This scaffold contains no gameplay code and no audio, model, texture or other
media. Do not place game files or third-party tools in this directory. Support
for an optional user-owned scream file may be added later, but is not currently
implemented.

The mod is designed to disable itself completely in multiplayer. Keep
singlePlayerOnly and disableInMultiplayer enabled. allowNetworkSends is a
locked safety declaration and future code must reject a true value.
