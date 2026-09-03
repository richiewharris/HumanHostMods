using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace HHMods.Weapons
{
    /// <summary>
    /// One-shot diagnostic: enumerates every <see cref="AnimationClip"/> currently loaded
    /// via <c>Resources.FindObjectsOfTypeAll</c>, groups them by likely category
    /// (attack / bow / hand / other), and writes to the BepInEx log plus a text file at
    /// <c>plugins/HHMods/animation-clips.txt</c>. Used to pick the right clip for the
    /// throw animation once, then hardcode it in config.
    ///
    /// Fires idempotently — safe to call every frame; only the first invocation with clips
    /// present actually runs.
    /// </summary>
    internal static class AnimationClipDumper
    {
        /// <summary>
        /// Runs the dump. <paramref name="force"/> = true re-dumps even if we've already dumped
        /// once (used by the F10 hotkey so we can capture clips that got streamed in after
        /// the first HubReady tick — e.g., axe chop clips only load when an axe is equipped).
        /// </summary>
        public static void Dump(bool force = false)
        {
            var clips = Resources.FindObjectsOfTypeAll<AnimationClip>();
            if (clips == null || clips.Length == 0)
            {
                if (force) Plugin.Log.LogWarning("[ClipDump] no AnimationClips loaded — nothing to dump");
                return;
            }

            var sb = new StringBuilder();
            sb.AppendLine("======================================================================");
            sb.AppendLine($"HHMods.Weapons — AnimationClip dump  ({System.DateTime.Now:yyyy-MM-dd HH:mm:ss})  force={force}");
            sb.AppendLine($"Total clips loaded: {clips.Length}");
            sb.AppendLine("======================================================================");

            // Bucket the clips by keyword so the sections we care about (attack/swing/throw)
            // are easy to find. Anything not matching a keyword lands in "other".
            var attackKW = new[] { "Chop", "Attack", "Swing", "Melee", "Hit", "Slash", "Punch", "Strike", "Axe", "Hammer", "Pickaxe" };
            var bowKW    = new[] { "Bow", "Arrow", "Pull", "Release", "Draw", "Shoot", "Fire" };
            var handKW   = new[] { "Hand", "Grip", "Grab", "Reach", "Throw", "Toss", "Cast" };

            var byBucket = new Dictionary<string, List<AnimationClip>>
            {
                ["ATTACK / MELEE"] = new List<AnimationClip>(),
                ["BOW / ARROW"] = new List<AnimationClip>(),
                ["HAND / THROW / GRIP"] = new List<AnimationClip>(),
                ["OTHER (first 60 shown)"] = new List<AnimationClip>(),
            };

            foreach (var c in clips)
            {
                if (c == null || string.IsNullOrEmpty(c.name)) continue;
                var n = c.name;
                if (attackKW.Any(k => n.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0))
                    byBucket["ATTACK / MELEE"].Add(c);
                else if (bowKW.Any(k => n.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0))
                    byBucket["BOW / ARROW"].Add(c);
                else if (handKW.Any(k => n.IndexOf(k, System.StringComparison.OrdinalIgnoreCase) >= 0))
                    byBucket["HAND / THROW / GRIP"].Add(c);
                else
                    byBucket["OTHER (first 60 shown)"].Add(c);
            }

            foreach (var kv in byBucket)
            {
                sb.AppendLine();
                sb.AppendLine($"---- {kv.Key} ---- ({kv.Value.Count})");
                var sorted = kv.Value.OrderBy(c => c.name).ToList();
                int show = kv.Key.StartsWith("OTHER") ? System.Math.Min(60, sorted.Count) : sorted.Count;
                for (int i = 0; i < show; i++)
                {
                    var c = sorted[i];
                    sb.AppendLine($"  {c.name,-50} len={c.length,6:F2}s frameRate={c.frameRate}Hz  legacy={c.legacy}  hasEvents={c.events != null && c.events.Length > 0}");
                }
                if (kv.Key.StartsWith("OTHER") && sorted.Count > show)
                    sb.AppendLine($"  ... {sorted.Count - show} more elided");
            }

            var body = sb.ToString();
            foreach (var line in body.Split('\n')) Plugin.Log.LogInfo(line.TrimEnd('\r'));

            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var outPath = Path.Combine(pluginDir ?? ".", "animation-clips.txt");
                File.WriteAllText(outPath, body);
                Plugin.Log.LogInfo($"[ClipDump] wrote {outPath}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[ClipDump] file write failed: {e.Message}"); }
        }
    }
}
