OsamaBinLaden user configuration
================================

On first installation, config.json.default is copied to config.json without
overwriting an existing configuration.

The plugin generates its low-poly character, scream and explosion at runtime.
The package contains no audio, model, texture, game asset or other media. Do not
place game files or third-party tools in this directory.

The mod is designed to disable itself completely in multiplayer. Keep
singlePlayerOnly and disableInMultiplayer are locked on. allowNetworkSends is a
locked safety declaration and the plugin always repairs it to false.
