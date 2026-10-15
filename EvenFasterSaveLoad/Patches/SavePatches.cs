using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Threading.Tasks;
using EvenFasterSaveLoad.Json;
using FullSerializer;
using HarmonyLib;

namespace EvenFasterSaveLoad.Patches
{
    /// <summary>
    /// SaveGame serializes three headers and then writes them with WriteSaveFile. Serialize() is replaced with
    /// the fast writer; inside SaveGame it returns a unique placeholder string instead of the JSON, and
    /// WriteSaveFile() streams the writer's UTF-8 buffer straight to disk when it sees that placeholder.
    /// </summary>
    [HarmonyPatch]
    internal static class SavePatches
    {
        private static readonly Stack<GraphWriter> Idle = new Stack<GraphWriter>();
        private static readonly ConditionalWeakTable<string, PendingWrite> Pending = new ConditionalWeakTable<string, PendingWrite>();
        private static int insideSaveGame;

        /// <summary>Size of the last document the fast writer produced, or -1 if vanilla handled it.</summary>
        public static int LastLength = -1;

        [HarmonyPrefix, HarmonyPatch(typeof(SaveController), nameof(SaveController.SaveGame))]
        private static void EnterSaveGame() => insideSaveGame++;

        [HarmonyFinalizer, HarmonyPatch(typeof(SaveController), nameof(SaveController.SaveGame))]
        private static void LeaveSaveGame() => insideSaveGame--;

        [HarmonyPrefix, HarmonyPatch(typeof(SaveController), nameof(SaveController.Serialize))]
        private static bool Serialize(Type type, object value, ref string __result)
        {
            LastLength = -1;
            if (!Plugin.FastSerializerActive)
                return true;

            GraphWriter writer = Idle.Count > 0 ? Idle.Pop() : new GraphWriter();
            writer.Threads = Plugin.WorkerThreads();
            if (!TryWrite(writer, type, value))
            {
                Idle.Push(writer);
                return true;
            }

            LastLength = writer.Output.Length;
            if (Plugin.LogLoadTimes.Value && writer.Output.Length >= 256 * 1024)
                Plugin.Log.LogInfo($"[Timing]   {type.Name} {writer.Output.Length / 1024} KB: " +
                                   (writer.UsedParallel ? $"{writer.Threads} threads" : $"one thread ({writer.SingleThreadReason})"));
            if (Plugin.VerifyAgainstVanilla.Value)
                Verify.Serialization(type, value, writer.Output.Decode());

            if (insideSaveGame > 0)
            {
                __result = new string("<EvenFasterSaveLoad: pending write>".ToCharArray()); // fresh instance = unique key
                Pending.Add(__result, new PendingWrite { Writer = writer, Type = type, Value = value });
            }
            else
            {
                __result = writer.Output.Decode();
                Idle.Push(writer);
            }
            return false;
        }

        private static bool TryWrite(GraphWriter writer, Type type, object value)
        {
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    writer.Serialize(type, value);
                    return true;
                }
                catch (FallbackException e)
                {
                    Plugin.Log.LogWarning($"Save: {type.Name} uses the vanilla serializer: {e.Message}");
                    return false;
                }
                catch (Exception e) when (attempt == 0 && Accessors.IsCodegenFailure(e) && !Accessors.Disabled)
                {
                    Plugin.Log.LogWarning($"Save: generated accessors rejected by the runtime, switching to reflection: {e.Message}");
                    Accessors.Disable();
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Save: {type.Name}: fast serializer failed, using vanilla: {e}");
                    return false;
                }
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(SaveController), nameof(SaveController.WriteSaveFile))]
        private static bool WriteSaveFile(string path, string content)
        {
            if (content == null || !Pending.TryGetValue(content, out PendingWrite pending))
                return true;
            Pending.Remove(content);
            try
            {
                if (NeedsSafetyCheck(path))
                {
                    string vanilla = SafetyCheck(Path.GetFileName(path), pending);
                    if (vanilla != null)
                    {
                        // The fast output differs: write exactly what the game itself would have written.
                        File.WriteAllText(path, vanilla, System.Text.Encoding.UTF8);
                        return false;
                    }
                }
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 16))
                {
                    file.Write(Utf8Bom, 0, Utf8Bom.Length); // File.WriteAllText(path, text, Encoding.UTF8) writes one too
                    pending.Writer.Output.WriteTo(file);
                }
            }
            finally
            {
                Idle.Push(pending.Writer);
            }
            return false;
        }

        internal sealed class PendingWrite
        {
            public GraphWriter Writer;
            public Type Type;
            public object Value;
        }

        // ---- safety check -------------------------------------------------------------------------------

        // Files already confirmed identical to vanilla in this session.
        private static readonly HashSet<string> Checked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool NeedsSafetyCheck(string path)
        {
            if (!Plugin.SafetyCheck.Value || Plugin.VerifyAgainstVanilla.Value) // verification already compares every save
                return false;
            // Once the serializer has tripped, anything it already produced for this save is checked too.
            return Plugin.FastSerializerTripped || !Checked.Contains(Path.GetFileName(path));
        }

        /// <summary>
        /// Runs the game's own serializer on the same object and compares. Returns null if identical, otherwise the
        /// vanilla JSON (and switches the fast serializer off for the rest of the session).
        /// </summary>
        internal static string SafetyCheck(string fileName, PendingWrite pending)
        {
            fsResult result = FsHost.Serializer.TrySerialize(pending.Type, pending.Value, out fsData data);
            result.AssertSuccessWithoutWarnings(); // where vanilla would fail, fail the same way
            string vanilla = fsJsonPrinter.CompressedJson(data);
            string fast = pending.Writer.Output.Decode();
            if (fast == vanilla)
            {
                Checked.Add(fileName);
                return null;
            }
            Plugin.TripFastSerializer($"{fileName} differed from the game's own output; saved the game's version instead.\n" +
                                      Verify.Describe(fast, vanilla));
            return vanilla;
        }

        private static readonly byte[] Utf8Bom = { 0xEF, 0xBB, 0xBF };

        // ---- copying the previous save folder -----------------------------------------------------------

        // private static void CopyDirectory(string sourceDir, string destinationDir, bool recursive)
        [HarmonyPrefix, HarmonyPatch(typeof(SaveController), nameof(SaveController.CopyDirectory))]
        private static bool CopyDirectory(string sourceDir, string destinationDir, bool recursive)
        {
            if (!Plugin.On(Plugin.ParallelCopy) || !recursive)
                return true;

            var source = new DirectoryInfo(sourceDir);
            if (!source.Exists)
                throw new DirectoryNotFoundException("Source directory not found: " + source.FullName);

            // SaveGame rewrites these four right after copying, so copying them is wasted I/O.
            var rewritten = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Info.tnmt", "Globals.tnmt", SaveController.LevelName + ".tnmt", "Globals.dialogue",
            };

            string root = source.FullName.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var jobs = new List<KeyValuePair<string, string>>();
            Directory.CreateDirectory(destinationDir);
            foreach (string dir in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories))
                Directory.CreateDirectory(Path.Combine(destinationDir, dir.Substring(root.Length + 1)));
            foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            {
                string relative = file.Substring(root.Length + 1);
                if (relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }) < 0 &&
                    rewritten.Contains(relative))
                {
                    continue;
                }
                jobs.Add(new KeyValuePair<string, string>(file, Path.Combine(destinationDir, relative)));
            }

            try
            {
                Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(2, Math.Min(Environment.ProcessorCount, 8)) },
                    job => File.Copy(job.Key, job.Value));
            }
            catch (AggregateException e)
            {
                ExceptionDispatchInfo.Capture(e.Flatten().InnerExceptions[0]).Throw();
            }
            return false;
        }
    }
}
