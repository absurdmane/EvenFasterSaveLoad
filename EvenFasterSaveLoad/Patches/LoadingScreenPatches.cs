using System.Collections;
using System.Diagnostics;
using HarmonyLib;
using UnityEngine;

namespace EvenFasterSaveLoad.Patches
{
    internal enum FadeMode
    {
        Vanilla,
        Fast,
        Off,
    }

    /// <summary>
    /// Loading-screen pacing. Nothing here changes what is saved or loaded, or in which order: the game still
    /// waits for its fades before saving and still hides the screen while loading; only how long that takes.
    /// </summary>
    [HarmonyPatch]
    internal static class LoadingScreenPatches
    {
        // ---- faster scene loading ----------------------------------------------------------------------

        private static bool raised;
        private static ThreadPriority originalPriority;
        private static int originalSlice;
        private static int originalBuffer;

        /// <summary>
        /// While the loading screen is up nobody is watching frame rate, so let Unity spend more of each frame on
        /// integrating loaded assets (background priority, texture upload time slice and buffer).
        /// </summary>
        [HarmonyPostfix, HarmonyPatch(typeof(LoadingScreen), nameof(LoadingScreen.LoadLevel))]
        private static void LoadLevel(LoadingScreen __instance)
        {
            if (!Plugin.On(Plugin.FastSceneLoading) || raised)
                return;
            raised = true;
            originalPriority = Application.backgroundLoadingPriority;
            originalSlice = QualitySettings.asyncUploadTimeSlice;
            originalBuffer = QualitySettings.asyncUploadBufferSize;
            Application.backgroundLoadingPriority = ThreadPriority.High;
            QualitySettings.asyncUploadTimeSlice = Mathf.Max(originalSlice, 8);
            QualitySettings.asyncUploadBufferSize = Mathf.Max(originalBuffer, 64);
            __instance.StartCoroutine(RestoreAfterLoad(__instance));
        }

        private static IEnumerator RestoreAfterLoad(LoadingScreen screen)
        {
            while (screen != null && !screen.LevelLoadDone)
                yield return null;
            Application.backgroundLoadingPriority = originalPriority;
            QualitySettings.asyncUploadTimeSlice = originalSlice;
            QualitySettings.asyncUploadBufferSize = originalBuffer;
            raised = false;
        }

        // ---- fades -------------------------------------------------------------------------------------

        // public void Fade(bool Fadein, bool soundsMuted, float delay, bool changeHint = true, string sceneName = "")
        // The game passes 0.5 s here before fading out after a load.
        [HarmonyPrefix, HarmonyPatch(typeof(LoadingScreen), nameof(LoadingScreen.Fade))]
        private static void ShortenFadeDelay(ref float delay)
        {
            switch (Mode)
            {
                case FadeMode.Fast: delay *= 0.25f; break; // a short pause hides the scene settling after a load
                case FadeMode.Off: delay = 0f; break;
            }
        }

        private static FadeMode Mode => Plugin.ModEnabled ? Plugin.LoadingFades.Value : FadeMode.Vanilla;

        private static CanvasGroup group;
        private static LoadingScreen groupOwner;
        private static float lastAlpha;

        private static readonly Stopwatch Transition = new Stopwatch();

        /// <summary>
        /// Called every frame from the plugin's Update, which Unity runs before resuming coroutines. The game's
        /// fade loops step the loading screen's alpha by 0.05 per frame until it reaches 1 or 0; when a fade is in
        /// progress this pushes it further in the same direction, so the game's own loop simply finishes sooner.
        /// Also times the whole transition the player sees (screen starts darkening -> fully visible again).
        /// </summary>
        public static void Tick()
        {
            LoadingScreen screen = LoadingScreen.instance;
            if (screen == null)
                return;
            if (screen != groupOwner)
            {
                groupOwner = screen;
                group = screen.GetComponent<CanvasGroup>();
                lastAlpha = group != null ? group.alpha : 0f;
            }
            if (group == null)
                return;

            float alpha = group.alpha;
            TrackTransition(alpha);

            FadeMode mode = Mode;
            if (mode != FadeMode.Vanilla)
            {
                // Fast: 4x the game's per-frame step. Deliberately per frame, not per second: the first frames after a
                // load are long hitches, and a frame-based fade stays up through them instead of revealing the jerks.
                float step = mode == FadeMode.Off ? 1f : 0.15f;
                if (alpha > lastAlpha && alpha < 1f)
                    group.alpha = Mathf.Min(1f, alpha + step);
                else if (alpha < lastAlpha && alpha > 0f)
                    group.alpha = Mathf.Max(0f, alpha - step);
            }
            lastAlpha = group.alpha;
        }

        private static void TrackTransition(float alpha)
        {
            if (!Transition.IsRunning && alpha > 0f && lastAlpha <= 0f)
            {
                Transition.Restart();
            }
            else if (Transition.IsRunning && alpha <= 0f)
            {
                Transition.Stop();
                // Ignore blips (menus can flash the canvas without loading anything).
                if (Transition.Elapsed.TotalSeconds >= 0.5)
                    Timing.ReportTransition(Transition.Elapsed.TotalSeconds, Mode);
            }
        }
    }
}
