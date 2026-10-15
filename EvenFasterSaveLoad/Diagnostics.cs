using System;
using System.IO;
using System.Text;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace EvenFasterSaveLoad
{
    /// <summary>
    /// Debug mode: mirrors this mod's warnings/errors/timings and every Unity error or exception into
    /// BepInEx/EvenFasterSaveLoad-debug.log, tagged with what was going on (saving/loading) and which serializer
    /// was active, so a bad save/load can be traced afterwards.
    /// </summary>
    internal static class Diagnostics
    {
        public static string Phase = "idle";

        private static readonly object Gate = new object();
        private static StreamWriter file;
        private static ManualLogSource source;

        public static string FilePath => Path.Combine(Paths.BepInExRootPath, "EvenFasterSaveLoad-debug.log");

        public static void Start(ManualLogSource log)
        {
            if (file != null)
                return;
            source = log;
            try
            {
                file = new StreamWriter(new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
            }
            catch (Exception e)
            {
                log.LogWarning($"Debug log unavailable ({FilePath}): {e.Message}");
                return;
            }
            Write("====", $"session start: {Plugin.Name} {Plugin.Version}, game {Application.version}, Unity {Application.unityVersion}, " +
                         $"{Environment.ProcessorCount} cores");
            source.LogEvent += OnModLog;
            Application.logMessageReceivedThreaded += OnUnityLog;
            log.LogInfo("Debug log: " + FilePath);
        }

        public static void Stop()
        {
            if (file == null)
                return;
            source.LogEvent -= OnModLog;
            Application.logMessageReceivedThreaded -= OnUnityLog;
            lock (Gate)
            {
                file.Dispose();
                file = null;
            }
        }

        public static void Note(string message) => Write("NOTE", message);

        private static void OnModLog(object sender, LogEventArgs e)
        {
            if ((e.Level & (LogLevel.Warning | LogLevel.Error | LogLevel.Fatal | LogLevel.Message)) != 0 ||
                (e.Data?.ToString().StartsWith("[Timing]", StringComparison.Ordinal) ?? false))
            {
                Write(e.Level.ToString().ToUpperInvariant(), e.Data?.ToString());
            }
        }

        private static void OnUnityLog(string condition, string stackTrace, LogType type)
        {
            if (type != LogType.Error && type != LogType.Exception && type != LogType.Assert)
                return;
            // Only errors that involve saving/loading; the game logs plenty of unrelated ones.
            string text = condition + "\n" + stackTrace;
            if (!IsSaveRelated(text))
                return;
            Write("UNITY " + type.ToString().ToUpperInvariant(), condition + ManagedFrames(stackTrace));
        }

        private static readonly string[] SaveMarkers =
        {
            "SaveController", "FullSerializer", "fsSerializer", "fsJson", "EvenFasterSaveLoad", ".tnmt",
            "MigrationController", "SavableScript", "DeSerializeData", "SerializeData",
        };

        private static bool IsSaveRelated(string text)
        {
            foreach (string marker in SaveMarkers)
            {
                if (text.IndexOf(marker, StringComparison.Ordinal) >= 0)
                    return true;
            }
            return false;
        }

        // Unity appends native frames ("0x00007ff... (UnityPlayer)") that say nothing useful here.
        private static string ManagedFrames(string stackTrace)
        {
            if (string.IsNullOrEmpty(stackTrace))
                return "";
            var sb = new StringBuilder();
            int kept = 0;
            foreach (string line in stackTrace.Split('\n'))
            {
                string frame = line.TrimEnd();
                if (frame.Length == 0 || frame.StartsWith("0x", StringComparison.Ordinal) || kept >= 20)
                    continue;
                sb.Append("\n  ").Append(frame);
                kept++;
            }
            return sb.ToString();
        }

        private static void Write(string level, string message)
        {
            lock (Gate)
            {
                if (file == null)
                    return;
                string mode = $"ser={(Plugin.FastSerializerActive ? "fast" : "vanilla")} deser={(Plugin.FastDeserializerActive ? "fast" : "vanilla")}";
                file.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{level}] [{Phase}] [{mode}] {message}");
            }
        }
    }
}
