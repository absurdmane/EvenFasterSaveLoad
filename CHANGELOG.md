# Changelog

## Unreleased

- Fast loading works again on game 0.5.08: the game started reading saves through a new method, so save files were being
  read the vanilla way. This includes the files with embedded asset references (Globals, the Mines, your apartment):
  the fast reader now repeats the game's new asset step itself.
- A level change right after a save no longer waits for the map image to be written (about 0.4 s when leaving the Mines).

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
