# Changelog

## 1.2.0

- Fixed problems caused by changes in game 0.5.08: save files were read the slow vanilla way.
- Fast reading now also covers save files with embedded assets (Globals, the Mines, your apartment): entering the Mines
  is about 1.5 s faster.
- A level change no longer waits for the map image to be written: about 0.4 s faster when leaving the Mines.
- Experimental `KeepLevelAssets` (off by default): keeps levels you left in memory, so going back to them is faster.
  Can give a good gain on an HDD, but may be slower on an SSD, and the game uses more memory.

## 1.1.0

- Removed the debug hotkeys (F10 / F11, `EnableKey` / `DisableKey`). The mod on/off switch for A/B tests is now a button
  in the dev-only DevMenu.
- `[Mod] Enabled`: switch the whole mod off and on from the config, without a restart.
- The timings window has its own setting, `TimingsWindow` (off by default), instead of always opening with `DebugMode`.

## 1.0.1

- Game 0.5.08: furniture saved through the game's `SavedAssetConverter` is written by the fast serializer again
  (it fell back to vanilla for the whole `SaveDataHeader`). Reading such objects stays with the game's own code.

## 1.0.0

- First release.
