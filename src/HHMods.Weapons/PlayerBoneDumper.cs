using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text;
using UnityEngine;

namespace HHMods.Weapons
{
    /// <summary>
    /// One-shot diagnostic: walks the local player character's Transform hierarchy and
    /// logs every bone (path, name, local position/rotation, hierarchy depth) to the
    /// BepInEx log plus a text file at <c>plugins/HHMods/player-bones.txt</c>. Also
    /// notes the Animator + AnimancerComponent structure and marks bones we're
    /// specifically interested in (Hand_R, Shoulder_R, Head, etc.) so custom throw
    /// animations can target the right transforms without guessing bone names.
    /// </summary>
    internal static class PlayerBoneDumper
    {
        // Bone names we care about for throw animations; any hit is marked in the dump.
        private static readonly string[] HighlightedTokens = new[]
        {
            "hand_r", "handr", "right_hand", "righthand",
            "hand_l", "handl", "left_hand", "lefthand",
            "shoulder_r", "shoulderr", "clavicle_r", "upperarm_r", "arm_r",
            "elbow_r", "forearm_r", "wrist_r",
            "head", "neck", "spine", "hips", "pelvis", "root",
            "index", "thumb", "middle", "ring", "pinky",
        };

        public static void Dump()
        {
            var players = Object.FindObjectsOfType<global::Player_Input>(includeInactive: false);
            if (players == null || players.Length == 0) { Plugin.Log.LogWarning("[BoneDump] no Player_Input in scene"); return; }
            var p = players[0];
            var charBase = p.GetComponent<global::C_Controller_Base>() ?? p.GetComponentInParent<global::C_Controller_Base>();
            var root = charBase != null ? charBase.transform : p.transform;

            var sb = new StringBuilder();
            sb.AppendLine("======================================================================");
            sb.AppendLine($"HHMods.Weapons — Player bone dump  ({System.DateTime.Now:yyyy-MM-dd HH:mm:ss})");
            sb.AppendLine($"Root: {GetPath(root)}");
            sb.AppendLine("======================================================================");

            // Header: components that matter for animation on the character root
            var animator = charBase?.animator ?? root.GetComponentInChildren<Animator>();
            var animancer = charBase?._3rd_Animancer;
            sb.AppendLine();
            sb.AppendLine("---- Animation components ----");
            sb.AppendLine($"  Animator            : {(animator != null ? animator.name : "(null)")}");
            sb.AppendLine($"  Animator controller : {(animator != null && animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "(null)")}");
            sb.AppendLine($"  Animator avatar     : {(animator != null && animator.avatar != null ? animator.avatar.name : "(null)")}");
            sb.AppendLine($"  Animator isHuman    : {(animator != null && animator.avatar != null ? animator.avatar.isHuman.ToString() : "(n/a)")}");
            sb.AppendLine($"  Animator hasRoot    : {(animator != null && animator.avatar != null ? animator.hasRootMotion.ToString() : "(n/a)")}");
            sb.AppendLine($"  Animancer component : {(animancer != null ? animancer.name : "(null)")}");
            sb.AppendLine($"  Basic body layer    : {(charBase?._basicBodyLayer != null ? "present" : "null")}");
            sb.AppendLine($"  Upper body layer    : {(charBase?._UpperBodyLayer != null ? "present" : "null")}");

            // Full bone hierarchy walk (depth-indented tree)
            sb.AppendLine();
            sb.AppendLine("---- Bone hierarchy ----");
            WalkTree(root, 0, sb);

            // Cross-reference: humanoid bone map, if the avatar is humanoid
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
            {
                sb.AppendLine();
                sb.AppendLine("---- Humanoid bone map (Animator.GetBoneTransform) ----");
                foreach (HumanBodyBones b in System.Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (b == HumanBodyBones.LastBone) continue;
                    Transform t = null;
                    try { t = animator.GetBoneTransform(b); } catch { }
                    if (t != null) sb.AppendLine($"  {b,-24} = {GetPath(t)}");
                }
            }

            var body = sb.ToString();
            foreach (var line in body.Split('\n')) Plugin.Log.LogInfo(line.TrimEnd('\r'));
            try
            {
                var pluginDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
                var outPath = Path.Combine(pluginDir ?? ".", "player-bones.txt");
                File.WriteAllText(outPath, body);
                Plugin.Log.LogInfo($"[BoneDump] wrote {outPath}");
            }
            catch (System.Exception e) { Plugin.Log.LogWarning($"[BoneDump] file write failed: {e.Message}"); }
        }

        private static void WalkTree(Transform t, int depth, StringBuilder sb)
        {
            if (t == null) return;
            var indent = new string(' ', depth * 2);
            var name = t.name ?? "(null)";
            var hilite = IsHighlighted(name) ? " **" : "";
            var lp = t.localPosition;
            var lr = t.localEulerAngles;
            sb.AppendLine($"  {indent}{name}{hilite}  lp=({lp.x:F2},{lp.y:F2},{lp.z:F2}) lr=({lr.x:F0},{lr.y:F0},{lr.z:F0})");
            for (int i = 0; i < t.childCount; i++) WalkTree(t.GetChild(i), depth + 1, sb);
        }

        private static bool IsHighlighted(string name)
        {
            var lower = name.ToLowerInvariant();
            foreach (var t in HighlightedTokens) if (lower.Contains(t)) return true;
            return false;
        }

        private static string GetPath(Transform t)
        {
            if (t == null) return "(null)";
            var stack = new List<string>();
            var cur = t;
            while (cur != null) { stack.Add(cur.name); cur = cur.parent; }
            stack.Reverse();
            return string.Join("/", stack);
        }
    }
}
