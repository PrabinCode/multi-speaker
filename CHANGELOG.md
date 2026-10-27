# Changelog

Each version's section is copied into its GitHub release as the highlights.

## v1.1.0

- **Sound source** now is your laptop's sound output: choosing a speaker in the app makes it the Windows default output, and changing the output in the Windows sound menu updates the app. Before, the two could disagree and the copied sound came from the wrong device.
- **Channel** for each speaker: *Both*, *Left* or *Right*, so two speakers can play as a real stereo pair. The source speaker uses its Windows balance while playing (restored on Stop and on exit); MultiSpeaker first checks once, with an inaudible tone, that this won't remove that side from the copied sound.
- **Help**: the new ? button in the title bar explains how to get started, what each setting and button does, and what to do when something sounds wrong.
- **Faster start**: speaker names are read once instead of many times; with some Bluetooth drivers this cut startup from about 12 to 4 seconds.
- **Fixed: other speakers too quiet with some sources.** Speakers whose volume Windows applies in software (rather than in the speaker) had that volume baked into the copied sound, so the other speakers were turned down twice. MultiSpeaker now undoes it.
- **Stop** now works during Sync delay and Match volume and cancels the measurement.
- **More reliable Sync delay and Match volume**: a speaker that can't be heard (for example an HDMI output with nothing attached) is now reported instead of being turned down to 0 %, the source's own chirp can no longer be mistaken for another speaker's, and Match volume changes at most 10 dB per step and never goes below 5 %.

## v1.0.0

First release.

- Play your PC's sound on several Bluetooth speakers at once, each with its own delay
- **Sync delay**: measures every speaker with your microphone and sets the delays automatically
- **Match volume**: measures every speaker with your microphone and makes them equally loud
- **Link volumes**: change one speaker's volume, or use your keyboard volume keys, and the others follow by the same ratio
- Continuous drift correction keeps the speakers in time over long sessions
- Recovers by itself when a speaker disconnects and reconnects
- Light and dark theme, tray icon, and remembered settings
