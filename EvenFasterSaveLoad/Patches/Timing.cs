using System;
using System.Diagnostics;
using HarmonyLib;

namespace EvenFasterSaveLoad.Patches
{
    /// <summary>
    /// LogLoadTimes: one summary line per save and per load, plus a line for each big file. Works in either mode,
    /// so fast and vanilla can be compared on the same save (mod on/off from DevMenu). Time spent by VerifyAgainstVanilla is
    /// excluded; when it's on, the vanilla time it measured is shown next to the fast one.
    /// Load = from LoadGameWithMigration until the game raises LoadingDone.
    /// </summary>
    [HarmonyPatch]
    internal static class Timing
    {
        private const int BigFileChars = 256 * 1024;

        private struct Mark
        {
            public long Start;
            public long VerifySpent;
        }

        private sealed class Totals
        {
            public readonly Stopwatch Clock = new Stopwatch();
            public double Ms;
            public double VanillaMs;
            public int Files;
            public int Checked;
            public int Differ;
            public bool Fast;
            public long VerifyAtStart;

            /// <summary>Wall-clock time minus whatever VerifyAgainstVanilla spent meanwhile.</summary>
            public double TotalMs => Clock.Elapsed.TotalMilliseconds - (Verify.SpentTicks - VerifyAtStart) * 1000.0 / Stopwatch.Frequency;
            public string Mode => Fast ? "mod on" : "vanilla";

            public void Begin(bool fast)
            {
                Ms = VanillaMs = 0;
                Files = 0;
                Checked = Verify.Checked;
                Differ = Verify.Differ;
                Fast = fast;
                VerifyAtStart = Verify.SpentTicks;
                Clock.Restart();
            }

            public string Summary(string what)
            {
                string mode = Fast ? "fast" : "vanilla";
                string line = $"{what} {Ms:0} ms ({mode}, {Files} files)";
                int checkedNow = Verify.Checked - Checked;
                if (Fast && checkedNow > 0)
                {
                    int differ = Verify.Differ - Differ;
                    line += $"; vanilla would take {VanillaMs:0} ms; verified {checkedNow} files: " +
                            (differ == 0 ? "all identical" : $"{differ} DIFFER");
                }
                return line;
            }
        }

        /// <summary>Timings are collected for the console (LogLoadTimes) or the debug overlay (DebugMode).</summary>
        private static bool Tracking => Plugin.LogLoadTimes.Value || Plugin.DebugMode.Value || Plugin.TimingsWindow;

        private static readonly Totals Load = new Totals();
        private static readonly Totals Save = new Totals();
        private static bool subscribed;
        private static int saveDepth;

        // ---- loading -------------------------------------------------------------------------------------

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(SaveController), nameof(SaveController.LoadGameWithMigration))]
        private static void LoadStarted()
        {
            Diagnostics.Phase = "loading";
            if (!subscribed)
            {
                SaveController.LoadingDone += LoadFinished;
                subscribed = true;
            }
            if (Tracking)
            {
                Phases.Clear();
                Load.Begin(Plugin.FastDeserializerActive);
                loadStart = Stopwatch.GetTimestamp();
            }
        }

        private static long loadStart;

        private static void LoadFinished()
        {
            Diagnostics.Phase = "idle";
            if (!Load.Clock.IsRunning)
                return;
            Load.Clock.Stop();
            // TEMPORARY (comparing the experiments): this load, kept for the transition line written when the screen is back.
            pendingLoad = $"load {Load.TotalMs / 1000:0.00} s (reading saves {Load.Ms / 1000:0.00} s) | {AllPhases()}";
            string phases = TopPhases();
            if (Plugin.LogLoadTimes.Value)
                Plugin.Log.LogMessage($"[Timing] Load {Load.TotalMs / 1000:0.00} s total; " + Load.Summary("reading saves") + phases);
            Plugin.LastLoad = $"Load: {Load.TotalMs / 1000:0.00} s (reading saves {Load.Ms / 1000:0.00} s, {Load.Mode})";
        }

        // The overload the game's loads use since 0.5.08 (the shorter one forwards to it).
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(SaveController), nameof(SaveController.Deserialize),
            new[] { typeof(string), typeof(Type), typeof(string), typeof(Exception), typeof(bool) },
            new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
        private static void DeserializeIn(out Mark __state) => __state = Begin();

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(SaveController), nameof(SaveController.Deserialize),
            new[] { typeof(string), typeof(Type), typeof(string), typeof(Exception), typeof(bool) },
            new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
        private static void DeserializeOut(Mark __state, string fileName, string serializedState)
        {
            if (!Tracking)
                return;
            int size = serializedState?.Length ?? 0;
            Record(Load, __state, Plugin.FastDeserializerActive, size >= BigFileChars, $"read {fileName} {size / 1024} KB");
        }

        // ---- saving --------------------------------------------------------------------------------------

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(SaveController), nameof(SaveController.SaveGame))]
        private static void SaveStarted()
        {
            if (saveDepth++ > 0)
                return;
            Diagnostics.Phase = "saving";
            if (Tracking)
                Save.Begin(Plugin.FastSerializerActive);
        }

        [HarmonyFinalizer, HarmonyPatch(typeof(SaveController), nameof(SaveController.SaveGame))]
        private static void SaveFinished()
        {
            if (--saveDepth > 0)
                return;
            Diagnostics.Phase = Load.Clock.IsRunning ? "loading" : "idle";
            if (!Save.Clock.IsRunning)
                return;
            Save.Clock.Stop();
            double total = Save.TotalMs;
            string phases = TopPhases();
            if (Plugin.LogLoadTimes.Value)
                Plugin.Log.LogMessage($"[Timing] Save {total:0} ms total; " + Save.Summary("writing saves") + phases);
            Plugin.LastSave = $"Save: {total / 1000:0.00} s (writing {Save.Ms / 1000:0.00} s, {Save.Mode})";
        }


        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(SaveController), nameof(SaveController.Serialize))]
        private static void SerializeIn(out Mark __state)
        {
            __state = Begin();
        }

        [HarmonyPostfix, HarmonyPriority(Priority.Last)]
        [HarmonyPatch(typeof(SaveController), nameof(SaveController.Serialize))]
        private static void SerializeOut(Mark __state, Type type, string __result)
        {
            if (!Tracking)
                return;
            int size = SavePatches.LastLength >= 0 ? SavePatches.LastLength : (__result?.Length ?? 0);
            Record(Save, __state, Plugin.FastSerializerActive, size >= BigFileChars, $"write {type.Name} {size / 1024} KB");
        }

        // ---- where the rest of a load goes ---------------------------------------------------------------

        private static readonly System.Collections.Generic.Dictionary<string, double> Phases =
            new System.Collections.Generic.Dictionary<string, double>();

        private static void AddPhase(string name, long since)
        {
            if (!Load.Clock.IsRunning && !Save.Clock.IsRunning)
                return;
            Phases.TryGetValue(name, out double ms);
            Phases[name] = ms + Ms(Stopwatch.GetTimestamp() - since);
        }

        /// <summary>The phases that took longest since the last report, e.g. "OnLoadingGame 812 ms, DeSerializeData 640 ms".</summary>
        private static string TopPhases()
        {
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>>(Phases);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            Phases.Clear();
            var parts = new System.Collections.Generic.List<string>();
            foreach (var kv in list)
            {
                if (parts.Count == 12 || kv.Value < 20)
                    break;
                parts.Add($"{kv.Key} {kv.Value:0} ms");
            }
            return parts.Count == 0 ? "" : "\n[Timing]   slowest phases: " + string.Join(", ", parts);
        }

        // private static void ExecuteSaveLoadFunctions(GameObject[] gameObjects, LoadSaveType loadSaveType, bool includeInactive = false)
        [HarmonyPrefix, HarmonyPatch(typeof(SaveController), nameof(SaveController.ExecuteSaveLoadFunctions))]
        private static void PhaseIn(out long __state) => __state = Stopwatch.GetTimestamp();

        [HarmonyPostfix, HarmonyPatch(typeof(SaveController), nameof(SaveController.ExecuteSaveLoadFunctions))]
        private static void PhaseOut(long __state, SaveController.LoadSaveType loadSaveType)
        {
            if (Tracking)
                AddPhase(loadSaveType.ToString(), __state);
        }

        // Called per saved object while the scene restores itself (inside the phases above).
        [HarmonyPrefix, HarmonyPatch(typeof(SaveController), nameof(SaveController.DeSerializeData))]
        private static void RestoreIn(out long __state) => __state = Stopwatch.GetTimestamp();

        [HarmonyPostfix, HarmonyPatch(typeof(SaveController), nameof(SaveController.DeSerializeData))]
        private static void RestoreOut(long __state)
        {
            if (Tracking)
                AddPhase("  of which DeSerializeData", __state);
        }

        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyPatch(typeof(NPC.WaypointGraph), nameof(NPC.WaypointGraph.MapWaypoints))]
        private static void WaypointsIn(out long __state) => __state = Stopwatch.GetTimestamp();

        [HarmonyPostfix, HarmonyPriority(Priority.Last), HarmonyPatch(typeof(NPC.WaypointGraph), nameof(NPC.WaypointGraph.MapWaypoints))]
        private static void WaypointsOut(long __state)
        {
            if (Tracking)
                AddPhase("NPC waypoint paths", __state);
        }

        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyPatch(typeof(SaveController), nameof(SaveController.CopyDirectory))]
        private static void CopyIn(out long __state)
        {
            copyDepth++;
            __state = Stopwatch.GetTimestamp();
        }

        // Vanilla CopyDirectory recurses into subfolders; only the outermost call is timed.
        [HarmonyFinalizer, HarmonyPatch(typeof(SaveController), nameof(SaveController.CopyDirectory))]
        private static void CopyOut(long __state)
        {
            if (--copyDepth == 0 && Tracking)
                AddPhase("copy previous save", __state);
        }

        private static int copyDepth;

        [HarmonyPrefix, HarmonyPatch(typeof(LoadingScreen), nameof(LoadingScreen.LoadLevel))]
        private static void SceneIn()
        {
            sceneStart = Stopwatch.GetTimestamp();
            fromScene = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name; // TEMPORARY (transition log)
            // TEMPORARY (finding where a level change spends its time): from the load call to the scene starting to load.
            if (loadStart != 0 && Tracking)
                AddPhase("before the scene (save files, migrations, fade)", loadStart);
            loadStart = 0;
        }

        // TEMPORARY (finding where a level change spends its time): the dialogue restore and the save migrations.
        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        [HarmonyPatch(typeof(PixelCrushers.DialogueSystem.PersistentDataManager), nameof(PixelCrushers.DialogueSystem.PersistentDataManager.ApplySaveData))]
        private static void DialogueIn(out long __state) => __state = Stopwatch.GetTimestamp();

        [HarmonyFinalizer]
        [HarmonyPatch(typeof(PixelCrushers.DialogueSystem.PersistentDataManager), nameof(PixelCrushers.DialogueSystem.PersistentDataManager.ApplySaveData))]
        private static void DialogueOut(long __state)
        {
            if (Tracking)
                AddPhase("dialogue restore (Lua)", __state);
        }

        [HarmonyPrefix, HarmonyPatch(typeof(MigrationController), nameof(MigrationController.RunMigrations))]
        private static void MigrationsIn(out long __state) => __state = Stopwatch.GetTimestamp();

        [HarmonyPostfix, HarmonyPatch(typeof(MigrationController), nameof(MigrationController.RunMigrations))]
        private static void MigrationsOut(long __state)
        {
            if (Tracking)
                AddPhase("save migrations", __state);
        }

        private static long sceneStart;

        [HarmonyPrefix, HarmonyPatch(typeof(SaveController), nameof(SaveController.ExecuteSaveLoadFunctions))]
        private static void FirstPhaseAfterScene()
        {
            // The first restore phase starts right after the scene finished loading.
            if (sceneStart != 0 && Tracking)
                AddPhase("scene load (Unity)", sceneStart);
            sceneStart = 0;
        }

        /// <summary>The whole transition as the player sees it: screen starts darkening -> fully visible again.</summary>
        public static void ReportTransition(double seconds, FadeMode fades)
        {
            if (!Tracking)
                return;
            string mode = Plugin.ModEnabled ? "mod on" : "vanilla";
            Plugin.LastTransition = $"Transition: {seconds:0.00} s ({mode}, fades {fades})";
            if (Plugin.LogLoadTimes.Value)
                Plugin.Log.LogMessage($"[Timing] Transition {seconds:0.00} s from screen fading out to fully back ({mode}, fades {fades})");
            WriteTransitionLine(seconds, fades);
        }

        // ---- TEMPORARY (comparing the experiments): one line per level change in BepInEx/EvenFasterSaveLoad-transitions.log --------
        // Remove before the release, with pendingLoad / fromScene / AllPhases.

        private static string pendingLoad;
        private static string fromScene;

        private static string AllPhases()
        {
            var list = new System.Collections.Generic.List<System.Collections.Generic.KeyValuePair<string, double>>(Phases);
            list.Sort((a, b) => b.Value.CompareTo(a.Value));
            var parts = new System.Collections.Generic.List<string>();
            foreach (var kv in list)
                if (kv.Value >= 10)
                    parts.Add($"{kv.Key.Trim()} {kv.Value / 1000:0.00}");
            return string.Join(", ", parts);
        }

        private static string Settings()
        {
            if (!Plugin.ModEnabled)
                return "MOD OFF";
            string On(bool v) => v ? "on" : "off";
            return $"KeepLevelAssets {On(Plugin.KeepLevelAssets.Value)}, " +
                   $"FastSceneLoading {On(Plugin.FastSceneLoading.Value)}, fast read {On(Plugin.FastDeserializerActive)}, fades {Plugin.LoadingFades.Value}";
        }

        private static void WriteTransitionLine(double seconds, FadeMode fades)
        {
            string to = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            string line = $"{DateTime.Now:HH:mm:ss}  {fromScene ?? "?"} -> {to}: {seconds:0.00} s | {pendingLoad ?? "load ?"} | {Settings()}";
            pendingLoad = null;
            try
            {
                string path = System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "EvenFasterSaveLoad-transitions.log");
                System.IO.File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Transition log: {e.Message}");
            }
        }

        // ---- shared --------------------------------------------------------------------------------------

        private static Mark Begin()
        {
            Verify.LastVanillaMs = -1;
            return new Mark { Start = Stopwatch.GetTimestamp(), VerifySpent = Verify.SpentTicks };
        }

        private static void Record(Totals totals, Mark mark, bool fast, bool big, string what)
        {
            double ms = Ms(Stopwatch.GetTimestamp() - mark.Start - (Verify.SpentTicks - mark.VerifySpent));
            totals.Ms += ms;
            totals.Files++;
            double vanilla = Verify.LastVanillaMs;
            if (vanilla >= 0)
                totals.VanillaMs += vanilla;
            if (big)
            {
                string compare = fast && vanilla >= 0 ? $" (vanilla {vanilla:0} ms)" : "";
                if (Plugin.LogLoadTimes.Value)
                    Plugin.Log.LogInfo($"[Timing]   {what}: {ms:0} ms {(fast ? "fast" : "vanilla")}{compare}");
            }
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
