using System.Collections.Generic;
using UnityEngine;

namespace HHMods.Weapons
{
    /// <summary>
    /// One keyframe of a procedural throw animation, expressed in Unity's Humanoid muscle
    /// space. Each field is a normalized muscle value roughly in [-1, +1] where -1 is one
    /// extreme of the range of motion and +1 is the other (Unity maps these to
    /// rig-specific rotations via the Avatar). Time is normalized 0..1 across the variant.
    ///
    /// Using muscle values instead of raw bone eulers means we don't have to know or care
    /// about how the Mixamo bones are oriented — Unity's HumanPoseHandler translates
    /// muscles into whatever local rotations this specific rig needs. Same values would
    /// look identical on any Humanoid rig.
    /// </summary>
    public struct ThrowKey
    {
        public float T;
        public float RArmDownUp;       // shoulder: -1 down at side, +1 straight up
        public float RArmFrontBack;    // shoulder: -1 behind body, +1 forward
        public float RArmTwist;        // shoulder: twist along bone
        public float RForearmStretch;  // elbow: -1 fully flexed, +1 fully extended
        public float RForearmTwist;    // forearm twist
        public float RHandDownUp;      // wrist: -1 down, +1 up
        public float RHandInOut;       // wrist: -1 in (toward body), +1 out

        public ThrowKey(float t, float armUp, float armFB, float armTw,
                        float foreStretch, float foreTw, float handUp, float handIO)
        { T = t; RArmDownUp = armUp; RArmFrontBack = armFB; RArmTwist = armTw;
          RForearmStretch = foreStretch; RForearmTwist = foreTw;
          RHandDownUp = handUp; RHandInOut = handIO; }
    }

    /// <summary>
    /// One named throwing style. Duration is total time; ProjectilePeakT is the fraction
    /// at which the projectile leaves the hand (typically the frame the arm swings past
    /// horizontal on the forward stroke).
    /// </summary>
    public class ThrowVariant
    {
        public string Name;
        public float Duration;
        public float ProjectilePeakT;
        public ThrowKey[] Keys;

        /// <summary>Smoothstep-interpolated muscle sample at normalized time.</summary>
        public ThrowKey Sample(float t01)
        {
            if (Keys == null || Keys.Length == 0) return new ThrowKey();
            if (t01 <= Keys[0].T) return Keys[0];
            if (t01 >= Keys[Keys.Length - 1].T) return Keys[Keys.Length - 1];
            for (int i = 0; i < Keys.Length - 1; i++)
            {
                var a = Keys[i]; var b = Keys[i + 1];
                if (t01 >= a.T && t01 <= b.T)
                {
                    var f = (t01 - a.T) / Mathf.Max(1e-4f, b.T - a.T);
                    var sm = f * f * (3f - 2f * f);
                    return new ThrowKey(
                        t01,
                        Mathf.Lerp(a.RArmDownUp,      b.RArmDownUp,      sm),
                        Mathf.Lerp(a.RArmFrontBack,   b.RArmFrontBack,   sm),
                        Mathf.Lerp(a.RArmTwist,       b.RArmTwist,       sm),
                        Mathf.Lerp(a.RForearmStretch, b.RForearmStretch, sm),
                        Mathf.Lerp(a.RForearmTwist,   b.RForearmTwist,   sm),
                        Mathf.Lerp(a.RHandDownUp,     b.RHandDownUp,     sm),
                        Mathf.Lerp(a.RHandInOut,      b.RHandInOut,      sm));
                }
            }
            return Keys[Keys.Length - 1];
        }
    }

    /// <summary>
    /// Library of seed throw variants. All in muscle space so they work regardless of rig
    /// bone orientation. Values are first-pass estimates informed by real throw kinematics:
    /// wind-up = arm raised + pulled back + elbow flexed; release = arm forward + elbow
    /// extending; follow-through = arm across body + elbow relaxed.
    /// </summary>
    public static class ThrowVariantLibrary
    {
        public static readonly List<ThrowVariant> All = new List<ThrowVariant>
        {
            // 1. OVERHAND BASEBALL — classic pitcher motion, deep windup over the shoulder,
            // hard forward release with elbow snap.
            new ThrowVariant
            {
                Name = "Overhand Baseball",
                Duration = 0.85f,
                ProjectilePeakT = 0.55f,
                Keys = new[]
                {
                    //           T     armUp  armFB  armTw  foreStretch  foreTw  handUp  handIO
                    new ThrowKey(0.00f,  0.0f,  0.0f,  0.0f,   0.0f,       0.0f,   0.0f,   0.0f),
                    new ThrowKey(0.35f,  0.85f,-0.60f, 0.30f,  -0.90f,     0.30f,  -0.50f,  0.20f),  // full windup
                    new ThrowKey(0.55f,  0.35f, 0.70f,-0.20f,   0.60f,     -0.40f,  0.30f, -0.10f), // release
                    new ThrowKey(0.80f, -0.15f, 0.40f,-0.10f,   0.10f,      0.00f,  0.10f,  0.10f), // follow-through
                    new ThrowKey(1.00f,  0.0f,  0.0f,  0.0f,   0.0f,       0.0f,   0.0f,   0.0f),
                },
            },
            // 2. SIDEARM WHIP — arm at shoulder height, whips horizontally across body.
            new ThrowVariant
            {
                Name = "Sidearm Whip",
                Duration = 0.70f,
                ProjectilePeakT = 0.50f,
                Keys = new[]
                {
                    new ThrowKey(0.00f,  0.0f,  0.0f,  0.0f,   0.0f,       0.0f,   0.0f,   0.0f),
                    new ThrowKey(0.30f,  0.10f,-0.70f, 0.50f,  -0.70f,     0.20f,   0.00f,  0.50f), // side windup
                    new ThrowKey(0.50f,  0.10f, 0.70f,-0.30f,   0.70f,    -0.30f,   0.00f, -0.30f), // side release
                    new ThrowKey(0.70f, -0.10f, 0.30f,-0.10f,   0.10f,     0.00f,   0.00f,  0.00f),
                    new ThrowKey(1.00f,  0.0f,  0.0f,  0.0f,   0.0f,       0.0f,   0.0f,   0.0f),
                },
            },
            // 3. UNDERHAND TOSS — arm sweeps down and back, then forward and up. Softball pitch.
            new ThrowVariant
            {
                Name = "Underhand Toss",
                Duration = 0.95f,
                ProjectilePeakT = 0.60f,
                Keys = new[]
                {
                    new ThrowKey(0.00f,  0.0f,  0.0f,  0.0f,   0.0f,       0.0f,   0.0f,   0.0f),
                    new ThrowKey(0.40f, -0.70f,-0.40f, 0.10f,  -0.30f,     0.00f,  -0.30f,  0.00f), // arm down and behind
                    new ThrowKey(0.60f, -0.20f, 0.70f,-0.10f,   0.50f,    -0.20f,   0.30f,  0.00f), // arm forward, palm up
                    new ThrowKey(0.85f,  0.10f, 0.50f, 0.00f,   0.10f,     0.00f,   0.10f,  0.00f),
                    new ThrowKey(1.00f,  0.0f,  0.0f,  0.0f,   0.0f,       0.0f,   0.0f,   0.0f),
                },
            },
            // 4. QUICK SNAP — small windup, fast forward release. Rapid-fire throws.
            new ThrowVariant
            {
                Name = "Quick Snap",
                Duration = 0.40f,
                ProjectilePeakT = 0.55f,
                Keys = new[]
                {
                    new ThrowKey(0.00f,  0.0f,  0.0f,  0.0f,   0.0f,       0.0f,   0.0f,   0.0f),
                    new ThrowKey(0.25f,  0.50f,-0.30f, 0.20f,  -0.70f,     0.20f,  -0.20f,  0.10f),
                    new ThrowKey(0.55f,  0.30f, 0.60f,-0.20f,   0.60f,    -0.30f,   0.20f,  0.00f),
                    new ThrowKey(1.00f,  0.0f,  0.0f,  0.0f,   0.0f,       0.0f,   0.0f,   0.0f),
                },
            },
        };
    }
}
