# Even Faster SaveLoad for Obenseuer

A BepInEx 6 (Unity Mono) plugin that makes saving and loading in **Obenseuer** faster, without changing the
save format: files written by the mod are byte-for-byte what the game itself would write, and the game can
still read them with the mod removed.

Named after, and inspired by, the original [FastSaveLoad by FireWarp](https://www.nexusmods.com/profile/FireWarp). 
This is an independent, open-source implementation of the same idea, written from scratch against FullSerializer's documented behavior.

## What it does

| Feature | Default | What changes |
|---|---|---|
| Fast serializer | on | Writes save JSON in two passes straight into a UTF-8 buffer instead of building FullSerializer's `fsData` tree and printing it. Large files are written by several threads. |
| Fast deserializer | on | Parses JSON into a flat token array and builds objects directly from it. |
| Parallel copy | on | Copies the previous save folder with several threads and skips the four files the game rewrites right after. |
| Async map PNG | on | Encodes and writes the explored-map PNG in the background. |
| Batched waypoints | on | Requests all NPC waypoint paths at once on scene load instead of waiting for each in turn. |
| Pathfinding threads | all cores | Gives the pathfinder up to 16 threads (all cores but one) when a scene asks for one. |
| Loading-screen fades | Fast | Fades to and from the loading screen 4x faster and shortens the half-second pause after a load. `Off` cuts instantly, `Vanilla` keeps the game's fades. |
| Fast scene loading | off | While the loading screen is up, lets Unity spend more time per frame loading the scene. |

Everything falls back to the game's own code when the mod meets something it can't reproduce exactly
(the reason is logged), so a fallback costs speed, never data.

`SafetyCheck` (on by default): the first write of each save file per session is compared with the game's own
serializer before it reaches the disk. On any difference the game's version is written and fast saving is
switched off until restart. Cost: one extra vanilla save per file per session (about +0.6 s for a 12 MB level).

## Results

Measured in game 0.5.05 on a 16-core desktop with a late-game save (12.5 MB Mines level), warm runs,
vanilla = the same session with the mod switched off (F11 in debug mode):

| | Vanilla | Mod |
|---|---|---|
| Loading the Mines | 11.1 s | 6.1–7.2 s |
| ↳ NPC waypoint paths | 3.8 s | 0.5–0.9 s |
| ↳ reading save files | 1.76 s | 0.31–0.38 s |
| Saving when leaving the Mines | 1.60 s | 0.42–0.55 s |
| Loading the Tenement | 4.9 s | 3.5–3.7 s |
| Saving when leaving the Tenement | 0.25 s | 0.10 s |
| Whole transition into the Mines, as seen by the player¹ | 12.0 s | 6.2–7.3 s |
| Whole transition into the Tenement¹ | 7.0 s | 4.0–4.1 s |
| Whole transition into the Bazaar¹ | 9.3 s | 5.8–5.9 s |
| Whole transition out to the Tenement street¹ | 6.6 s | 3.5–3.9 s |
| Whole transition into Kolhola¹ | 5.4 s | 3.8 s |

¹ From the screen starting to fade out until the game is fully visible again (save + load + fades),
The Mines and Tenement rows were measured with `LoadingFades = Off`, the others with the default `Fast`
(which adds a few tenths of a second).

The rest of a load is Unity loading the scene itself, which the mod doesn't touch. Slower CPUs and HDDs
spend proportionally more time in the parts the mod speeds up.

## Install

1. Install [BepInEx 6 (Unity Mono)](https://builds.bepinex.dev/projects/bepinex_be) into the game folder.
2. Put `EvenFasterSaveLoad.dll` into `BepInEx/plugins/EvenFasterSaveLoad/`.
3. Start the game once; settings appear in `BepInEx/config/obenseuer.evenfastersaveload.cfg`.

Don't install it together with the original FastSaveLoad: both patch the same methods, so BepInEx will skip this one if the original is present.

## Debugging and benchmarking

In the `[Debug]` section of the config:

- `DebugMode` — enables hotkeys **F10** (fast serializer on) / **F11** (vanilla) for A/B tests without
  restarting, and writes `BepInEx/EvenFasterSaveLoad-debug.log` with the mod's warnings plus every Unity error and
  exception, tagged with the current phase (saving/loading) and active mode.
- `LogLoadTimes` — logs total save and load times and per-file (de)serialization times to the BepInEx console,
  in either mode.
- `VerifyAgainstVanilla` — runs the vanilla serializer next to the fast one and reports the first difference. Slow.

When reporting a problem, please attach `BepInEx/EvenFasterSaveLoad-debug.log` and `BepInEx/LogOutput.log`.

## Building

Requirements: .NET SDK 6+ and the game with BepInEx 6 installed.

1. Copy `local.props.example` to `local.props` and set `GameDir` to your Obenseuer folder.
2. `dotnet build EvenFasterSaveLoad -c Release` (add `-p:Deploy=true` to copy the DLL into the game's plugins folder).
3. `dotnet run -c Release --project EvenFasterSaveLoad.Tests` runs the offline equivalence tests: the game's own
   FullSerializer and this mod are run side by side on the same objects and JSON, and must agree byte for byte.
   Pass save file paths after `--` to benchmark parsing on real saves.

## How it stays compatible

FullSerializer numbers every tracked object in first-encounter order, writes `{"$ref":"n"}` for repeat
encounters, and adds `"$id":"n"` to a definition only if a reference to it shows up later. The writer
therefore does a census pass first (also where `OnBeforeSerialize` callbacks fire), then emits final JSON in a
second pass. The census records the id counter at each element of large lists under the root, so ranges of
those lists can be emitted on separate threads and simply concatenated.

The reader mirrors `fsSerializer.TryDeserialize` step by step (references, `$type`, abstract-type remapping,
instance reuse, `$id` registration, converter semantics, callbacks). Before creating anything for a node it
checks that the data has exactly the shape the converter expects; otherwise that node is handed to the vanilla
deserializer, which also produces vanilla's warnings, so the game's migration logic triggers exactly as before.

## License

MIT © absurdmane, see [LICENSE](LICENSE).
