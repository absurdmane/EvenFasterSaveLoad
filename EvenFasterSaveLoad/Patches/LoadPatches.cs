using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Diagnostics;
using EvenFasterSaveLoad.Json;
using FullSerializer;
using HarmonyLib;
using Debug = UnityEngine.Debug;

namespace EvenFasterSaveLoad.Patches
{
    /// <summary>
    /// Fast reading of save files. The game has two Deserialize overloads: since 0.5.08 the loads go through the one with
    /// <c>skippedFieldsOnly</c>, which also runs <c>SavedAssets.CollapseGraphs</c> on the parsed tree (it turns embedded
    /// ScriptableObject assets marked with <c>addressableAssetPath</c> into references to the loaded asset). The fast reader
    /// makes the same rewrite while reading, for files that contain <c>addressableAssetPath</c> at all.
    /// </summary>
    [HarmonyPatch]
    internal static class LoadPatches
    {
        private const string AssetPathKey = "\"addressableAssetPath\"";

        private static readonly TapeReader Reader = new TapeReader();

        // ---- game version guard -------------------------------------------------------------------------
        //
        // The fast reader repeats SavedAssets' rewrite by hand. If a game update changes any of these methods, the copy may no
        // longer match, so files with assets go back to the game until the mod is checked against the new code. The hash is
        // over the methods' IL; a recompiled game changes it even without real changes, which only costs speed.

        private static readonly string[] CollapseMethods =
        {
            "CollapseGraphs", "IndexDefinitions", "Rewrite", "Replace", "CopyWithStubs", "Stub", "IsReference", "IsCollapsible",
            "MatchesType", "GetAsset",
        };

        private const string CollapseHash = "453321D418AC7D9A172C1EEBB17D935129A292CCA78344389737B7B07B8D5213"; // game 0.5.08

        private static bool? collapseKnown;

        private static bool CollapseKnown => collapseKnown ??= CheckCollapseCode();

        private static bool CheckCollapseCode()
        {
            string hash;
            try
            {
                var il = new List<byte>();
                foreach (string name in CollapseMethods)
                    il.AddRange(AccessTools.Method(typeof(SavedAssets), name).GetMethodBody().GetILAsByteArray());
                using (SHA256 sha = SHA256.Create())
                    hash = BitConverter.ToString(sha.ComputeHash(il.ToArray())).Replace("-", "");
            }
            catch (Exception e)
            {
                hash = e.GetType().Name + ": " + e.Message;
            }
            if (hash == CollapseHash)
                return true;
            Plugin.Log.LogWarning("The game's SavedAssets code changed since this mod was made: save files with asset references " +
                                  $"(Globals, the Mines, ...) are read the vanilla way. ({hash})");
            return false;
        }
        private static bool reading;

        // public static object Deserialize(string fileName, Type type, string serializedState, out Exception exception)
        // (still used for small files such as Info.tnmt; it forwards to the overload below)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SaveController), nameof(SaveController.Deserialize),
            new[] { typeof(string), typeof(Type), typeof(string), typeof(Exception) },
            new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out })]
        private static bool Deserialize(string fileName, Type type, string serializedState, ref Exception exception, ref object __result) =>
            !TryRead(fileName, type, serializedState, ref exception, out _, ref __result);

        // public static object Deserialize(string fileName, Type type, string serializedState, out Exception exception, out bool skippedFieldsOnly)
        [HarmonyPrefix]
        [HarmonyPatch(typeof(SaveController), nameof(SaveController.Deserialize),
            new[] { typeof(string), typeof(Type), typeof(string), typeof(Exception), typeof(bool) },
            new[] { ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Normal, ArgumentType.Out, ArgumentType.Out })]
        private static bool DeserializeWithSkipped(string fileName, Type type, string serializedState, ref Exception exception,
            ref bool skippedFieldsOnly, ref object __result)
        {
            if (!TryRead(fileName, type, serializedState, ref exception, out bool skipped, ref __result))
                return true;
            skippedFieldsOnly = skipped;
            return false;
        }

        /// <summary>
        /// The fast read with the vanilla contract. False = leave the file to the game. <paramref name="skippedFieldsOnly"/> as vanilla
        /// sets it: the read went through and produced an object, but with warnings (e.g. fields that no longer exist).
        /// </summary>
        private static bool TryRead(string fileName, Type type, string serializedState, ref Exception exception, out bool skippedFieldsOnly,
            ref object __result)
        {
            skippedFieldsOnly = false;
            // The reader isn't re-entrant; a nested call (from a deserialization callback) goes to vanilla.
            if (!Plugin.FastDeserializerActive || string.IsNullOrEmpty(serializedState) || reading)
                return false;
            bool collapseAssets = serializedState.IndexOf(AssetPathKey, StringComparison.Ordinal) >= 0;
            if (collapseAssets && !CollapseKnown)
                return false;

            object value;
            fsResult result;
            try
            {
                reading = true;
                value = Reader.Read(type, serializedState, collapseAssets, out result);
            }
            catch (FallbackException e)
            {
                Plugin.Log.LogWarning($"Load: {fileName} uses the vanilla deserializer: {e.Message}");
                return false;
            }
            catch (Exception e)
            {
                if (Accessors.IsCodegenFailure(e) && !Accessors.Disabled)
                {
                    Plugin.Log.LogWarning($"Load: generated accessors rejected by the runtime, switching to reflection: {e.Message}");
                    Accessors.Disable();
                }
                Plugin.Log.LogWarning($"Load: {fileName}: fast deserializer failed, using vanilla: {e}");
                return false;
            }
            finally
            {
                reading = false;
            }

            // Same contract as vanilla: any warning becomes `exception`, which sends the game into migration
            // (unless only fields were skipped).
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
            skippedFieldsOnly = exception != null && result.Succeeded && value != null;
            __result = value;

            if (Plugin.VerifyAgainstVanilla.Value)
                Verify.Deserialization(fileName, type, serializedState, value);
            return true;
        }
    }
}
