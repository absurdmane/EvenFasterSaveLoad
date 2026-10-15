using System;
using System.Diagnostics;
using EvenFasterSaveLoad.Json;
using FullSerializer;
using HarmonyLib;
using Debug = UnityEngine.Debug;

namespace EvenFasterSaveLoad.Patches
{
    [HarmonyPatch]
    internal static class LoadPatches
    {
        private static readonly TapeReader Reader = new TapeReader();
        private static bool reading;

        // public static object Deserialize(string fileName, Type type, string serializedState, out Exception exception)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SaveController), nameof(SaveController.Deserialize),
            new[] { typeof(string), typeof(Type), typeof(string), typeof(Exception) },
            new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
        private static bool Deserialize(string fileName, Type type, string serializedState, ref Exception exception, ref object __result)
        {
            // The reader isn't re-entrant; a nested call (from a deserialization callback) goes to vanilla.
            if (!Plugin.FastDeserializerActive || string.IsNullOrEmpty(serializedState) || reading)
                return true;

            object value;
            fsResult result;
            try
            {
                reading = true;
                value = Reader.Read(type, serializedState, out result);
            }
            catch (FallbackException e)
            {
                Plugin.Log.LogWarning($"Load: {fileName} uses the vanilla deserializer: {e.Message}");
                return true;
            }
            catch (Exception e)
            {
                if (Accessors.IsCodegenFailure(e) && !Accessors.Disabled)
                {
                    Plugin.Log.LogWarning($"Load: generated accessors rejected by the runtime, switching to reflection: {e.Message}");
                    Accessors.Disable();
                }
                Plugin.Log.LogWarning($"Load: {fileName}: fast deserializer failed, using vanilla: {e}");
                return true;
            }
            finally
            {
                reading = false;
            }

            // Same contract as vanilla: any warning becomes `exception`, which sends the game into migration.
            exception = null;
            try
            {
                result.AssertSuccessWithoutWarnings();
            }
            catch (Exception e)
            {
                exception = e;
                Debug.LogError($"{fileName}: {e}");
                Debug.LogException(e);
            }
            __result = value;

            if (Plugin.VerifyAgainstVanilla.Value)
                Verify.Deserialization(fileName, type, serializedState, value);
            return false;
        }
    }
}
