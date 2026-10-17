using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using HarmonyLib;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace EvenFasterSaveLoad.Patches
{
    /// <summary>
    /// Experimental (<c>KeepLevelAssets</c>, off by default): trade memory for faster returns to a level.
    /// A level change unloads the old scene, and Unity then frees every asset nothing references any more; going back reads the
    /// textures, meshes and sounds from disk again and uploads the textures to the GPU again. Here, just before a level change,
    /// the assets the old scene uses (meshes, materials, their textures, audio clips) are referenced from a static list, so Unity
    /// keeps them; when the scene loads again it finds them already in memory. The scene itself is rebuilt by the game as usual
    /// (objects, save data), so nothing about the game's logic changes.
    /// Levels are kept as long as the game stays under the memory limit (<c>KeepLevelAssetsMemoryGb</c>); over it, the oldest
    /// level is let go after a load and Unity is asked to free its assets.
    /// </summary>
    [HarmonyPatch]
    internal static class AssetCache
    {
        private sealed class Level
        {
            public string Scene;
            public HashSet<Object> Assets;
        }

        // Oldest first.
        private static readonly List<Level> kept = new List<Level>();
        private static readonly List<int> textureIds = new List<int>();

        // Auto limit: the PC's RAM minus this much for Windows and other programs.
        private const double ReservedForSystemGb = 6;

        private static bool Active => Plugin.ModEnabled && Plugin.KeepLevelAssets.Value;

        /// <summary>The game's memory limit in bytes: the setting, or auto (RAM minus 6 GB) when it's 0.</summary>
        private static double LimitBytes
        {
            get
            {
                double gb = Plugin.KeepLevelAssetsMemoryGb.Value;
                if (gb <= 0)
                    gb = SystemInfo.systemMemorySize / 1024.0 - ReservedForSystemGb;
                return gb * 1024 * 1024 * 1024;
            }
        }

        // Process.PrivateMemorySize64 is always 0 under Unity's Mono, so ask Windows directly.
        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessMemoryCounters
        {
            public uint Size;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("psapi.dll", SetLastError = true)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, out ProcessMemoryCounters counters, uint size);

        /// <summary>The game's private memory in bytes, as Task Manager shows it; -1 if it can't be read (then the limit isn't applied).</summary>
        private static long GameMemory()
        {
            try
            {
                uint size = (uint)Marshal.SizeOf(typeof(ProcessMemoryCounters));
                return GetProcessMemoryInfo(GetCurrentProcess(), out ProcessMemoryCounters c, size) ? (long)c.PrivateUsage.ToUInt64() : -1;
            }
            catch (Exception)
            {
                return -1;
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(LoadingScreen), nameof(LoadingScreen.LoadLevel))]
        private static void BeforeLevelChange()
        {
            try
            {
                Remember();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"KeepLevelAssets: could not collect the level's assets: {e.Message}");
            }
        }

        /// <summary>Takes the assets of the level being left, while its scene is still loaded.</summary>
        private static void Remember()
        {
            if (!Active)
            {
                Clear();
                return;
            }
            var clock = Stopwatch.StartNew();
            string scene = SceneManager.GetActiveScene().name;
            var assets = new HashSet<Object>();
            var materials = new HashSet<Material>();

            foreach (Renderer renderer in Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            {
                foreach (Material material in renderer.sharedMaterials)
                    if (material != null)
                        materials.Add(material);
                if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
                    assets.Add(skinned.sharedMesh);
            }
            foreach (MeshFilter filter in Object.FindObjectsByType<MeshFilter>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (filter.sharedMesh != null)
                    assets.Add(filter.sharedMesh);
            foreach (AudioSource source in Object.FindObjectsByType<AudioSource>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (source.clip != null)
                    assets.Add(source.clip);
            foreach (Material material in materials)
            {
                assets.Add(material);
                material.GetTexturePropertyNameIDs(textureIds);
                foreach (int id in textureIds)
                {
                    Texture texture = material.GetTexture(id);
                    if (texture != null)
                        assets.Add(texture);
                }
            }

            kept.RemoveAll(l => l.Scene == scene);
            kept.Add(new Level { Scene = scene, Assets = assets });
            if (Plugin.LogLoadTimes.Value)
                Plugin.Log.LogInfo($"[KeepLevelAssets] kept {assets.Count} assets of {scene} ({clock.ElapsedMilliseconds} ms); " +
                                   $"levels kept: {string.Join(", ", kept.Select(l => l.Scene))}");
        }

        /// <summary>
        /// After a level loaded: its own entry isn't needed (its scene holds those assets now). Over the memory limit, the oldest
        /// level is let go and Unity frees what nothing uses any more; one level per load, since the memory only drops once the
        /// assets are actually freed. Trimming here, not before the load, keeps the level being entered in memory while it loads.
        /// </summary>
        internal static void AfterLoad()
        {
            if (!Active)
            {
                Clear();
                return;
            }
            string scene = SceneManager.GetActiveScene().name;
            kept.RemoveAll(l => l.Scene == scene);

            long memory = GameMemory();
            double limit = LimitBytes;
            // TEMPORARY (measuring KeepLevelAssets): the game's memory after each level load. Remove once the defaults are decided.
            if (Plugin.LogLoadTimes.Value)
                Plugin.Log.LogInfo($"[KeepLevelAssets] {scene} loaded; game memory {memory / 1e9:0.00} GB, limit {limit / 1e9:0.0} GB, " +
                                   $"levels kept: {(kept.Count == 0 ? "none" : string.Join(", ", kept.Select(l => l.Scene)))}");

            if (memory < 0 || memory <= limit || kept.Count == 0)
                return;
            string dropped = kept[0].Scene;
            kept.RemoveAt(0);
            Resources.UnloadUnusedAssets();
            Plugin.Log.LogInfo($"[KeepLevelAssets] over the memory limit ({memory / 1e9:0.00} of {limit / 1e9:0.0} GB): let go of {dropped}");
        }

        /// <summary>Lets everything go; Unity frees it on its next cleanup (the next level change at the latest).</summary>
        internal static void Clear() => kept.Clear();
    }
}
