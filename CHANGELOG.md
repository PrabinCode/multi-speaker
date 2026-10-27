# Changelog

Each version's section is copied into its GitHub release as the highlights.

## Unreleased

- **Sound source** now is your laptop's sound output: choosing a speaker in the app makes it the Windows default output, and changing the output in the Windows sound menu updates the app. Before, the two could disagree and the copied sound came from the wrong device.

## v1.0.0

First release.

- Play your PC's sound on several Bluetooth speakers at once, each with its own delay
- **Sync delay**: measures every speaker with your microphone and sets the delays automatically
- **Match volume**: measures every speaker with your microphone and makes them equally loud
- **Link volumes**: change one speaker's volume, or use your keyboard volume keys, and the others follow by the same ratio
- Continuous drift correction keeps the speakers in time over long sessions
- Recovers by itself when a speaker disconnects and reconnects
- Light and dark theme, tray icon, and remembered settings
