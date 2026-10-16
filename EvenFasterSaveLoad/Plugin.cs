using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.Mono;
using EvenFasterSaveLoad.Patches;
using HarmonyLib;
using UnityEngine;

namespace EvenFasterSaveLoad
{
    [BepInPlugin(Guid, Name, Version)]
    [BepInIncompatibility("FastSaveLoad")] // FireWarp's original FastSaveLoad patches the same methods
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Guid = "obenseuer.absurdmane.evenfastersaveload";

        /// <summary>The GUID before 1.0.2; its config file is taken over on the first start.</summary>
        private const string OldGuid = "obenseuer.evenfastersaveload";
        public const string Name = "Even Faster SaveLoad";
        public const string Version = "1.1.0";

        internal static ManualLogSource Log;

        internal static ConfigEntry<bool> FastSerializer;
        internal static ConfigEntry<bool> FastDeserializer;
        internal static ConfigEntry<int> Threads;
        internal static ConfigEntry<bool> SafetyCheck;
        internal static ConfigEntry<bool> ParallelCopy;
        internal static ConfigEntry<bool> AsyncMapPng;
        internal static ConfigEntry<bool> BatchedWaypoints;
        internal static ConfigEntry<int> PathfindingThreads;
        internal static ConfigEntry<bool> FastSceneLoading;
        internal static ConfigEntry<FadeMode> LoadingFades;
        internal static ConfigEntry<bool> DebugMode;
        internal static ConfigEntry<bool> LogLoadTimes;
        internal static ConfigEntry<bool> VerifyAgainstVanilla;

        /// <summary>Master switch ([Mod] Enabled; off = plain vanilla game), also flipped from DevMenu. Works without a restart.</summary>
        internal static ConfigEntry<bool> Enabled;
        private static bool modEnabled = true;

        /// <summary>A feature is active when it's enabled in the config and the debug master switch is on.</summary>
        internal static bool On(ConfigEntry<bool> feature) => modEnabled && feature.Value;

        internal static bool ModEnabled => modEnabled;

        internal static bool FastSerializerActive => On(FastSerializer) && !FastSerializerTripped;

        /// <summary>Set when the safety check caught a difference: fast saving stays off until the game restarts.</summary>
        internal static bool FastSerializerTripped { get; private set; }

        internal static void TripFastSerializer(string reason)
        {
            FastSerializerTripped = true;
            Log?.LogError("Safety check: " + reason + "\nThe fast serializer is off until the game restarts. Please report this with BepInEx/LogOutput.log.");
        }
        internal static bool FastDeserializerActive => On(FastDeserializer);

        private Harmony harmony;
        private static string banner;
        private static float bannerUntil;
        private GUIStyle bannerStyle;

        private void Awake()
        {
            Log = Logger;
            TakeOverOldConfig();
            BindConfig();
            Log.LogEvent += OnModLog;
            if (DebugMode.Value)
                Diagnostics.Start(Log);
            DebugMode.SettingChanged += (_, __) =>
            {
                if (DebugMode.Value)
                    Diagnostics.Start(Log);
                else
                    Diagnostics.Stop();
            };

            harmony = new Harmony(Guid);
            foreach (Type patches in new[]
                     {
                         typeof(SavePatches), typeof(LoadPatches), typeof(MapPngPatches),
                         typeof(PathfindingPatches), typeof(LoadingScreenPatches), typeof(Timing),
                     })
            {
                // A game update breaking one patch shouldn't take the other features with it.
                try
                {
                    harmony.CreateClassProcessor(patches).Patch();
                }
                catch (Exception e)
                {
                    Log.LogError($"{patches.Name} could not be applied and is disabled: {e}");
                }
            }

            Application.quitting += MapPngPatches.WaitForWrite;
            Log.LogInfo($"{Name} {Version} ready");
        }

        /// <summary>The GUID changed in 1.0.2 and the config file is named after it: copy the old settings once.</summary>
        private void TakeOverOldConfig()
        {
            try
            {
                string old = System.IO.Path.Combine(Paths.ConfigPath, OldGuid + ".cfg");
                if (System.IO.File.Exists(Config.ConfigFilePath) || !System.IO.File.Exists(old))
                    return;
                System.IO.File.Copy(old, Config.ConfigFilePath);
                Config.Reload();
                Log.LogInfo($"Settings taken over from {OldGuid}.cfg");
            }
            catch (Exception e)
            {
                Log.LogWarning($"Could not take over the old settings: {e.Message}");
            }
        }

        private void OnDestroy()
        {
            Application.quitting -= MapPngPatches.WaitForWrite;
            harmony?.UnpatchSelf();
            Diagnostics.Stop();
        }

        private void Update()
        {
            LoadingScreenPatches.Tick();
            TakePendingNotice();
        }

        internal static void SetModEnabled(bool enabled)
        {
            modEnabled = enabled;
            if (Enabled != null && Enabled.Value != enabled)
                Enabled.Value = enabled; // saved, so a restart keeps it
            string state = enabled ? "ON (all enabled features)" : "OFF (vanilla game; pathfinding threads apply from the next scene)";
            Log.LogMessage($"[Debug] {Name} {state}");
            banner = $"{Name}: {state}";
            bannerUntil = Time.realtimeSinceStartup + 3f;
        }

        internal static ConfigEntry<bool> ShowTimings;

        /// <summary>The timings window: from the config, also switched (and saved) from the dev menu (DevMenu class).</summary>
        internal static bool TimingsWindow
        {
            get => ShowTimings != null && ShowTimings.Value;
            set => ShowTimings.Value = value;
        }

        private Rect timingsRect = new Rect(10, 10, 390, 110);

        /// <summary>Last load/save timings, shown in the timings window (set by Timing).</summary>
        internal static string LastLoad = "Load: -";
        internal static string LastSave = "Save: -";
        internal static string LastTransition = "Transition: -";

        private GUIStyle panelStyle;

        private void OnGUI()
        {
            if (bannerStyle == null)
            {
                bannerStyle = new GUIStyle(GUI.skin.box) { fontSize = 20, alignment = TextAnchor.MiddleCenter };
                bannerStyle.normal.textColor = Color.white;
                panelStyle = new GUIStyle(GUI.skin.box) { fontSize = 14, alignment = TextAnchor.UpperLeft, padding = new RectOffset(8, 8, 6, 6) };
                panelStyle.normal.textColor = Color.white;
            }

            DrawNotice();
            if (TimingsWindow)
                timingsRect = GUI.Window(0x0EF5, timingsRect, DrawTimings, Name);
            if (!DebugMode.Value)
                return;

            if (banner != null && Time.realtimeSinceStartup <= bannerUntil)
                GUI.Box(new Rect(Screen.width / 2f - 260, 40, 520, 44), banner, bannerStyle);
        }

        // The last load/save durations; draggable while the cursor is free.
        private void DrawTimings(int id)
        {
            GUILayout.Label($"Mod: {(modEnabled ? "ON" : "OFF")}\n{LastTransition}\n{LastLoad}\n{LastSave}", panelStyle);
            GUI.DragWindow();
        }

        // ---- on-screen notices --------------------------------------------------------------------------

        private const float NoticeSeconds = 8f;
        private static readonly object NoticeGate = new object();
        private static string pendingNotice;
        private static bool pendingIsError;
        private static int pendingExtra;

        private string notice;
        private bool noticeIsError;
        private int noticeExtra;
        private float noticeUntil;
        private GUIStyle noticeStyle;

        /// <summary>
        /// This mod's own errors are always shown in game for a few seconds (players should know to send the log);
        /// warnings such as "this file went through the vanilla serializer" only in debug mode.
        /// May be raised from any thread, so it's handed to the main thread through a pending slot.
        /// </summary>
        private static void OnModLog(object sender, LogEventArgs e)
        {
            bool error = (e.Level & (LogLevel.Error | LogLevel.Fatal)) != 0;
            bool warning = (e.Level & LogLevel.Warning) != 0;
            if (!error && !(warning && DebugMode != null && DebugMode.Value))
                return;
            string text = e.Data?.ToString() ?? "";
            int newline = text.IndexOf('\n');
            if (newline >= 0)
                text = text.Substring(0, newline);
            if (text.Length > 160)
                text = text.Substring(0, 157) + "...";
            lock (NoticeGate)
            {
                if (pendingNotice != null && pendingIsError && !error)
                    return; // don't let a warning hide an error
                if (pendingNotice != null)
                    pendingExtra++;
                pendingNotice = text;
                pendingIsError = error;
            }
        }

        private void TakePendingNotice()
        {
            lock (NoticeGate)
            {
                if (pendingNotice == null)
                    return;
                bool stillShowing = notice != null && Time.realtimeSinceStartup <= noticeUntil;
                if (stillShowing && noticeIsError && !pendingIsError)
                {
                    noticeExtra++;
                }
                else
                {
                    noticeExtra = (stillShowing ? noticeExtra + 1 : 0) + pendingExtra;
                    notice = pendingNotice;
                    noticeIsError = pendingIsError;
                }
                noticeUntil = Time.realtimeSinceStartup + NoticeSeconds;
                pendingNotice = null;
                pendingExtra = 0;
            }
        }

        private void DrawNotice()
        {
            if (notice == null || Time.realtimeSinceStartup > noticeUntil)
                return;
            if (noticeStyle == null)
            {
                noticeStyle = new GUIStyle(GUI.skin.box) { fontSize = 16, wordWrap = true, alignment = TextAnchor.MiddleCenter, padding = new RectOffset(10, 10, 8, 8) };
                noticeStyle.normal.textColor = Color.white;
                noticeStyle.normal.background = Texture2D.whiteTexture;
            }
            string title = noticeIsError ? $"{Name}: error" : $"{Name}: warning";
            string more = noticeExtra > 0 ? $"  (+{noticeExtra} more)" : "";
            string text = $"{title}{more}\n{notice}\nDetails: BepInEx/LogOutput.log";
            Color previous = GUI.backgroundColor;
            GUI.backgroundColor = noticeIsError ? new Color(0.65f, 0.1f, 0.1f, 0.92f) : new Color(0.6f, 0.45f, 0.05f, 0.92f);
            float width = Mathf.Min(760f, Screen.width - 40f);
            GUI.Box(new Rect((Screen.width - width) / 2f, 96, width, 84), text, noticeStyle);
            GUI.backgroundColor = previous;
        }

        /// <summary>Threads for writing large save files; the main thread waits anyway, so it counts as one.</summary>
        internal static int WorkerThreads()
        {
            int configured = Threads.Value;
            return configured > 0 ? configured : Math.Max(1, Math.Min(Environment.ProcessorCount, 16));
        }

        private void BindConfig()
        {
            FastSerializer = Config.Bind("Serialization", "FastSerializer", true,
                "Write saves with the streaming serializer (byte-identical to the game's own output).");
            FastDeserializer = Config.Bind("Serialization", "FastDeserializer", true,
                "Read saves with the streaming parser instead of building FullSerializer's fsData tree.");
            Threads = Config.Bind("Serialization", "Threads", 0,
                new ConfigDescription("Threads used to write big save files. 0 = all cores, 1 = single-threaded.",
                    new AcceptableValueRange<int>(0, 64)));
            SafetyCheck = Config.Bind("Serialization", "SafetyCheck", true,
                "Before the first write of each save file per session, compare it with what the game itself would write. On any difference the game's version is written and fast saving switches off until restart. Costs one extra vanilla save per file per session.");

            ParallelCopy = Config.Bind("Saving", "ParallelCopy", true,
                "Copy the previous save folder with several threads, skipping files that get rewritten anyway.");
            AsyncMapPng = Config.Bind("Saving", "AsyncMapPng", true,
                "Encode and write the explored-map PNG in the background.");

            BatchedWaypoints = Config.Bind("Loading", "BatchedWaypoints", true,
                "Request all NPC waypoint paths at once instead of one after another.");
            PathfindingThreads = Config.Bind("Loading", "PathfindingThreads", 0,
                new ConfigDescription("Pathfinding threads when a scene asks for one. 0 = all cores but one (up to 16), 1 = leave the game's setting.",
                    new AcceptableValueRange<int>(0, 16)));
            FastSceneLoading = Config.Bind("Loading", "FastSceneLoading", false,
                "While the loading screen is up, let Unity spend more time per frame loading the scene (background priority, texture upload budget). May make the loading animation stutter.");
            LoadingFades = Config.Bind("Loading", "LoadingFades", FadeMode.Fast,
                "Loading-screen fades. Vanilla = as in the game, Fast = 4x faster fades and a shorter pause after a load, Off = instant cut to and from the loading screen.");

            Enabled = Config.Bind("Mod", "Enabled", true,
                "Off = the mod does nothing: saving and loading as in vanilla (e.g. to compare). Works without a restart; " +
                "pathfinding threads change from the next scene.");
            modEnabled = Enabled.Value;
            Enabled.SettingChanged += (_, __) =>
            {
                if (Enabled.Value != modEnabled)
                    SetModEnabled(Enabled.Value);
            };

            DebugMode = Config.Bind("Debug", "DebugMode", false,
                "An error log at BepInEx/EvenFasterSaveLoad-debug.log with this mod's warnings plus all Unity errors and exceptions.");
            ShowTimings = Config.Bind("Debug", "TimingsWindow", false,
                "A small window with the last load/save durations.");
            LogLoadTimes = Config.Bind("Debug", "LogLoadTimes", false,
                "Log save/load durations (whole operation and per file) to the BepInEx console, in either mode.");
            VerifyAgainstVanilla = Config.Bind("Debug", "VerifyAgainstVanilla", false,
                "Also run the vanilla serializer on every save/load and log differences. Slow; for testing.");
        }
    }
}
