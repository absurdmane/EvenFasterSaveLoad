using System;
using System.Diagnostics;
using EvenFasterSaveLoad.Json;
using FullSerializer;

namespace EvenFasterSaveLoad.Patches
{
    /// <summary>
    /// Debug option: run vanilla next to the fast path, log only differences, and keep counters plus the
    /// vanilla timings so <see cref="Timing"/> can report fast vs. vanilla from the same run.
    /// </summary>
    internal static class Verify
    {
        public static int Checked;
        public static int Differ;

        /// <summary>Total time spent verifying (excluded from the fast path's timings).</summary>
        public static long SpentTicks;

        /// <summary>Vanilla time for the most recent file, or -1.</summary>
        public static double LastVanillaMs = -1;

        public static void Serialization(Type type, object value, string fast)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                FsHost.Serializer.TrySerialize(type, value, out fsData data);
                string vanilla = fsJsonPrinter.CompressedJson(data);
                LastVanillaMs = Ms(Stopwatch.GetTimestamp() - start);
                Compare($"save {type.Name}", fast, vanilla);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Verify: save {type.Name} could not be checked: {e}");
            }
            SpentTicks += Stopwatch.GetTimestamp() - start;
        }

        public static void Deserialization(string fileName, Type type, string json, object fast)
        {
            long start = Stopwatch.GetTimestamp();
            try
            {
                fsSerializer fs = FsHost.Serializer;
                object vanilla = null;
                fs.TryDeserialize(fsJsonParser.Parse(json), type, ref vanilla);
                LastVanillaMs = Ms(Stopwatch.GetTimestamp() - start);
                // Compare both object graphs through vanilla's own printer.
                fs.TrySerialize(type, fast, out fsData a);
                fs.TrySerialize(type, vanilla, out fsData b);
                Compare($"load {fileName}", fsJsonPrinter.CompressedJson(a), fsJsonPrinter.CompressedJson(b));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"Verify: load {fileName} could not be checked: {e}");
            }
            SpentTicks += Stopwatch.GetTimestamp() - start;
        }

        private static void Compare(string what, string fast, string vanilla)
        {
            Checked++;
            if (fast == vanilla)
                return;
            Differ++;
            Plugin.Log.LogError($"Verify: {what} DIFFERS from vanilla. {Describe(fast, vanilla)}");
        }

        /// <summary>Where two documents first differ, with some context, for bug reports.</summary>
        public static string Describe(string fast, string vanilla)
        {
            int at = 0;
            while (at < fast.Length && at < vanilla.Length && fast[at] == vanilla[at])
                at++;
            return $"First difference at char {at}\n  fast:    {Excerpt(fast, at)}\n  vanilla: {Excerpt(vanilla, at)}";
        }

        private static string Excerpt(string s, int at)
        {
            int from = Math.Max(0, at - 80);
            return s.Substring(from, Math.Min(200, s.Length - from));
        }

        private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
    }
}
