using System.ComponentModel;

namespace EvenFasterSaveLoad
{
    /// <summary>
    /// Buttons for the dev-only DevMenu tool, which finds this class by name. Nothing here runs without it,
    /// and neither mod references the other.
    /// </summary>
    internal static class DevMenu
    {
        public static string Status() =>
            $"Timings window: {(Plugin.TimingsWindow ? "on" : "off")}   Mod: {(Plugin.ModEnabled ? "ON" : "OFF")}\n" +
            $"{Plugin.LastTransition}\n{Plugin.LastLoad}\n{Plugin.LastSave}";

        [Description("Timings window on / off")]
        public static void ToggleTimings() => Plugin.TimingsWindow = !Plugin.TimingsWindow;

        [Description("Mod on / off (A/B against the vanilla game)")]
        public static void ToggleMod() => Plugin.SetModEnabled(!Plugin.ModEnabled);
    }
}
