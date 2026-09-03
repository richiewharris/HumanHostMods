using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;

namespace HHMods.QoL.ChatBox
{
    /// <summary>
    /// Captures every NotificationSystem.Add_Notice call (all 4 overloads) and forwards
    /// to <see cref="ChatBox"/>, classifying each message into a <see cref="NoticeTag"/>.
    /// A companion patch on <c>Show_Text</c> optionally suppresses the game's own popup UI.
    /// </summary>
    [HarmonyPatch]
    internal static class NotificationInterceptor
    {
        internal static int InterceptCount;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var methods = typeof(global::NotificationSystem)
                .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                .Where(m => m.Name == "Add_Notice")
                .Cast<MethodBase>()
                .ToList();
            HHMods.Core.Plugin.Log?.LogInfo($"[NotificationInterceptor] targeting {methods.Count} Add_Notice overload(s)");
            return methods;
        }

        [HarmonyPostfix]
        private static void Postfix(string text)
        {
            InterceptCount++;
            if (string.IsNullOrEmpty(text)) return;
            var tag = Classify(text);
            HHMods.Core.Plugin.Log?.LogInfo($"[NotificationInterceptor] #{InterceptCount} ({tag}) \"{text}\"");
            ChatBox.Post(text, tag);
        }

        /// <summary>Route into tags based on text shape. Cheap; runs once per notification.</summary>
        private static NoticeTag Classify(string text)
        {
            // Pickups: "[ Name ] x Count" — verified via Show_Pickup_Notice IL.
            if (text.Length > 3 && text[0] == '[' && text[1] == ' '
                && text.IndexOf(" ] x ", System.StringComparison.Ordinal) > 0)
                return NoticeTag.Pickup;

            // Broadcasts: game's world-event text starts with "Survivor Broadcast".
            if (text.StartsWith("Survivor Broadcast", System.StringComparison.Ordinal))
                return NoticeTag.Broadcast;

            // Warnings: heuristic on nudge-style hints ("Shields can only be used ...").
            if (text.IndexOf("can only", System.StringComparison.OrdinalIgnoreCase) >= 0
             || text.IndexOf("cannot", System.StringComparison.OrdinalIgnoreCase) >= 0
             || text.IndexOf("not enough", System.StringComparison.OrdinalIgnoreCase) >= 0
             || text.IndexOf("must ", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return NoticeTag.Warning;

            return NoticeTag.Info;
        }
    }

    /// <summary>
    /// Suppresses the game's own on-screen notification bubble by short-circuiting
    /// NotificationSystem.Show_Text. Our Add_Notice postfix (above) still fires
    /// because Show_Text is called FROM inside Add_Notice, and Prefix-returning-false
    /// only skips Show_Text itself, not its caller.
    /// </summary>
    [HarmonyPatch(typeof(global::NotificationSystem), "Show_Text")]
    internal static class SuppressGameNotifBubble
    {
        [HarmonyPrefix]
        private static bool Prefix()
        {
            // Return true = run original (game shows bubble). False = skip (bubble suppressed).
            return !Plugin.SuppressGameNotifBubbles.Value;
        }
    }
}
