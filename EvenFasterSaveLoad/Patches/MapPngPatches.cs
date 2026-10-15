using System;
using System.IO;
using System.Threading.Tasks;
using HarmonyLib;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace EvenFasterSaveLoad.Patches
{
    /// <summary>
    /// Vanilla encodes the revealed-map PNG on the main thread during every save. The pixels still have to be
    /// read back on the main thread, but encoding and writing move to a worker. Everything that might read,
    /// copy or delete the save folder waits for the write first.
    /// </summary>
    [HarmonyPatch]
    internal static class MapPngPatches
    {
        private static readonly object Gate = new object();
        private static Task pending = Task.CompletedTask;

        public static void WaitForWrite()
        {
            Task task;
            lock (Gate)
                task = pending;
            if (task.IsCompleted)
                return;
            Plugin.Log.LogInfo("Map PNG: waiting for the background write");
            task.Wait();
        }

        [HarmonyPrefix, HarmonyPatch(typeof(RevealCamera), nameof(RevealCamera.OnSavingFile))]
        private static bool OnSavingFile(RevealCamera __instance, ref bool __result)
        {
            if (!Plugin.On(Plugin.AsyncMapPng))
                return true;
            __result = true;
            if (!__instance.hasSaveableData)
                return false;

            RenderTexture source = info_map.instance.renderTexture;
            string path = SaveController.instance.GetCurrentSavePath() + "/" + source.name + ".png";
            Debug.Log("Saving UI map " + path);

            Texture2D readback = new Texture2D(source.width, source.height, TextureFormat.ARGB32, false, false);
            RenderTexture previous = RenderTexture.active;
            try
            {
                RenderTexture.active = source;
                readback.ReadPixels(new Rect(0, 0, source.width, source.height), 0, 0);
            }
            finally
            {
                RenderTexture.active = previous;
            }

            // Copy out of the texture: the worker must not touch Unity objects.
            var pixels = new NativeArray<byte>(readback.GetRawTextureData<byte>(), Allocator.Persistent);
            GraphicsFormat format = readback.graphicsFormat;
            uint width = (uint)readback.width, height = (uint)readback.height;
            UnityEngine.Object.Destroy(readback);

            WaitForWrite();
            lock (Gate)
                pending = Task.Run(() => Encode(pixels, format, width, height, path));
            return false;
        }

        private static void Encode(NativeArray<byte> pixels, GraphicsFormat format, uint width, uint height, string path)
        {
            try
            {
                using (NativeArray<byte> png = ImageConversion.EncodeNativeArrayToPNG(pixels, format, width, height))
                using (var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    file.Write(png.ToArray(), 0, png.Length);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"Map PNG: writing {path} failed: {e}");
            }
            finally
            {
                pixels.Dispose();
            }
        }

        [HarmonyPrefix, HarmonyPatch(typeof(RevealCamera), nameof(RevealCamera.OnLoadingGame))]
        private static void BeforeMapLoad() => WaitForWrite();

        [HarmonyPrefix, HarmonyPatch(typeof(SaveController), nameof(SaveController.LoadGameWithMigration))]
        private static void BeforeLoadGame() => WaitForWrite();

        [HarmonyPrefix, HarmonyPatch(typeof(SaveController), nameof(SaveController.RemoveCharacter))]
        private static void BeforeDelete() => WaitForWrite();

        // First prefix on SaveGame: it may delete or copy the folder the PNG is being written into.
        [HarmonyPrefix, HarmonyPriority(Priority.First), HarmonyPatch(typeof(SaveController), nameof(SaveController.SaveGame))]
        private static void BeforeSave() => WaitForWrite();
    }
}
