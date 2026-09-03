using UnityEngine;

namespace HHMods.Weapons
{
    /// <summary>
    /// LateUpdate-driven Humanoid muscle override for the right arm. Uses Unity's
    /// <c>HumanPoseHandler</c> to read the character's current muscle-space pose, splice
    /// in the variant's right-arm muscle values on top of whatever the base animation is
    /// doing, and write the modified pose back. Runs in LateUpdate so it lands after the
    /// Animator/Animancer step and overrides the arm cleanly.
    ///
    /// Muscle-space authoring means the same variant looks correct on any Humanoid rig,
    /// regardless of how the FBX author oriented the bones. If we later import a real
    /// Mixamo throw FBX, its muscle values will drop in without axis remapping.
    /// </summary>
    public class ProceduralThrowRunner : MonoBehaviour
    {
        private Animator _animator;
        private HumanPoseHandler _handler;
        private HumanPose _pose;
        private ThrowVariant _variant;
        private float _startTime;
        private bool _active;

        // Cached muscle indices. Resolved once per Animator instance from HumanTrait
        // (which is stable across Unity versions).
        private int _iArmUp, _iArmFB, _iArmTw;
        private int _iForeStretch, _iForeTw;
        private int _iHandUp, _iHandIO;
        private Animator _cachedFor;

        public bool IsActive => _active;
        public string ActiveVariantName => _variant?.Name;

        public void Play(global::C_Controller_Base charBase, ThrowVariant variant)
        {
            if (charBase == null || variant == null) return;
            var animator = charBase.animator;
            if (animator == null) { Plugin.Log.LogWarning("[Throw] no Animator on player"); return; }
            if (animator.avatar == null || !animator.avatar.isHuman)
            {
                Plugin.Log.LogWarning("[Throw] Animator avatar is not Humanoid — procedural throw skipped");
                return;
            }

            // (Re)initialize the handler if the animator changed since last time.
            if (_animator != animator)
            {
                _animator = animator;
                try { _handler?.Dispose(); } catch { }
                _handler = new HumanPoseHandler(animator.avatar, animator.transform);
                _pose = new HumanPose();
                ResolveMuscleIndices();
                _cachedFor = animator;

                // Frustum-culling fix: when we drive the arm way out of its neutral pose,
                // the SkinnedMeshRenderer's pre-computed bounds don't cover the new arm
                // position, so Unity culls the whole character. Forcing updateWhenOffscreen
                // makes Unity recompute bounds every frame from live bone positions.
                foreach (var smr in animator.GetComponentsInChildren<SkinnedMeshRenderer>(includeInactive: true))
                {
                    smr.updateWhenOffscreen = true;
                }
                Plugin.Log.LogInfo($"[Throw] handler initialized; {animator.GetComponentsInChildren<SkinnedMeshRenderer>().Length} SMRs set to update-offscreen");
            }

            _variant = variant;
            _startTime = Time.time;
            _active = true;
        }

        private void ResolveMuscleIndices()
        {
            _iArmUp       = FindMuscle("Right Arm Down-Up");
            _iArmFB       = FindMuscle("Right Arm Front-Back");
            _iArmTw       = FindMuscle("Right Arm Twist In-Out");
            _iForeStretch = FindMuscle("Right Forearm Stretch");
            _iForeTw      = FindMuscle("Right Forearm Twist In-Out");
            _iHandUp      = FindMuscle("Right Hand Down-Up");
            _iHandIO      = FindMuscle("Right Hand In-Out");
            Plugin.Log.LogInfo($"[Throw] muscle indices: armUp={_iArmUp} armFB={_iArmFB} armTw={_iArmTw} foreStretch={_iForeStretch} foreTw={_iForeTw} handUp={_iHandUp} handIO={_iHandIO}");
        }

        private static int FindMuscle(string name)
        {
            var names = HumanTrait.MuscleName;
            for (int i = 0; i < names.Length; i++) if (names[i] == name) return i;
            Plugin.Log.LogWarning($"[Throw] muscle '{name}' not found in HumanTrait.MuscleName");
            return -1;
        }

        private void LateUpdate()
        {
            if (!_active || _variant == null || _handler == null) return;
            var elapsed = Time.time - _startTime;
            var t01 = elapsed / Mathf.Max(0.01f, _variant.Duration);
            if (t01 >= 1f) { _active = false; _variant = null; return; }

            var k = _variant.Sample(t01);
            _handler.GetHumanPose(ref _pose);
            var m = _pose.muscles;
            if (m == null || m.Length == 0)
            {
                Plugin.Log.LogWarning("[Throw] pose.muscles null/empty after GetHumanPose — aborting frame");
                return;
            }

            // Save body pos + rot: the HumanPose round-trip can subtly rewrite these on some
            // rigs (esp. when the animator's transform differs from the humanoid root),
            // which teleports the character. Preserve them exactly.
            var savedBodyPos = _pose.bodyPosition;
            var savedBodyRot = _pose.bodyRotation;

            if (_iArmUp       >= 0 && _iArmUp       < m.Length) m[_iArmUp]       = k.RArmDownUp;
            if (_iArmFB       >= 0 && _iArmFB       < m.Length) m[_iArmFB]       = k.RArmFrontBack;
            if (_iArmTw       >= 0 && _iArmTw       < m.Length) m[_iArmTw]       = k.RArmTwist;
            if (_iForeStretch >= 0 && _iForeStretch < m.Length) m[_iForeStretch] = k.RForearmStretch;
            if (_iForeTw      >= 0 && _iForeTw      < m.Length) m[_iForeTw]      = k.RForearmTwist;
            if (_iHandUp      >= 0 && _iHandUp      < m.Length) m[_iHandUp]      = k.RHandDownUp;
            if (_iHandIO      >= 0 && _iHandIO      < m.Length) m[_iHandIO]      = k.RHandInOut;

            _pose.bodyPosition = savedBodyPos;
            _pose.bodyRotation = savedBodyRot;
            _handler.SetHumanPose(ref _pose);
        }

        private void OnDestroy()
        {
            try { _handler?.Dispose(); } catch { }
        }
    }
}
