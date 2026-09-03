using HHMods.Core;
using UnityEngine;

namespace HHMods.Weapons
{
    /// <summary>
    /// First-pass throwing mechanic. Hold the throw key to charge, release to launch a
    /// physics sphere from the main camera along its forward axis. Muzzle velocity scales
    /// linearly between <c>MinThrowForce</c> at zero charge and <c>MaxThrowForce</c> at
    /// full charge.
    ///
    /// Deliberately minimal for a validation prototype:
    /// - No character-side animation. The player model does not raise its arm; the
    ///   projectile just appears at the camera and flies.
    /// - No inventory cost. Every throw is free. Will be gated on an actual item once we
    ///   flesh out throwables (rocks, mines, grenades) as real Item_Info entries.
    /// - No damage on impact. Projectile is a passive physics ball. Damage integration
    ///   comes with the mine / grenade work that builds on top of this pipeline.
    /// - Placeholder visual: bare Unity primitive sphere. HDRP will render it as magenta
    ///   with the fallback shader — that's fine as a visibility marker for now; a
    ///   TrailRenderer is added so the trajectory is legible even if the sphere is not.
    /// </summary>
    public class ThrowController : MonoBehaviour
    {
        private bool _charging;
        private float _chargeStart;
        private int _variantCycleIdx;
        private ProceduralThrowRunner _runner;

        private void Awake()
        {
            _runner = gameObject.AddComponent<ProceduralThrowRunner>();
        }

        private void Update()
        {
            // Wait for the game world to be up. Charging on the main menu makes no sense
            // and we don't have a camera to spawn from anyway.
            if (MgrHub.HubOrNull == null) return;

            // Manual dump on hotkey — clips stream in lazily as weapons get equipped, so
            // running once at HubReady misses the axe / bow / spear clips. Equip a weapon,
            // do a swing, then press the dump key.
            if (Plugin.ClipDumpKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.ClipDumpKey.Value))
                AnimationClipDumper.Dump(force: true);

            // Bone hierarchy dump for custom animation authoring.
            if (Plugin.BoneDumpKey.Value != KeyCode.None && Input.GetKeyDown(Plugin.BoneDumpKey.Value))
                PlayerBoneDumper.Dump();

            var key = Plugin.ThrowKey.Value;
            if (key == KeyCode.None) return;

            var down = Input.GetKey(key);
            if (down && !_charging)
            {
                _charging = true;
                _chargeStart = Time.unscaledTime;
            }
            else if (!down && _charging)
            {
                _charging = false;
                var charge = Mathf.Clamp01((Time.unscaledTime - _chargeStart) / Plugin.MaxChargeSeconds.Value);
                StartCoroutine(ThrowSequence(charge));
            }
        }

        /// <summary>
        /// Picks a throw variant (cycling through the library), starts the procedural arm
        /// animation on the player, waits until the variant's <see cref="ThrowVariant.ProjectilePeakT"/>
        /// moment, then launches the projectile from the right hand's world position along
        /// camera-forward. Posts the variant name to ChatBox (soft dep) so the player can
        /// rate which motion reads best in play.
        /// </summary>
        private System.Collections.IEnumerator ThrowSequence(float charge01)
        {
            var charBase = ResolvePlayerCharBase();

            if (Plugin.EnableThrowAnimation.Value)
            {
                var variant = PickNextVariant();
                if (charBase != null && variant != null && _runner != null)
                {
                    _runner.Play(charBase, variant);
                    NotifyChatBox($"Throw variant: {variant.Name}  (dur={variant.Duration:F2}s peak={variant.ProjectilePeakT:F2})");
                    Plugin.Log.LogInfo($"[Throw] variant '{variant.Name}' started (dur={variant.Duration:F2}s peakT={variant.ProjectilePeakT:F2})");
                    // Wait to the peak of the swing before launching.
                    var waitTime = variant.Duration * variant.ProjectilePeakT;
                    if (waitTime > 0f) yield return new WaitForSeconds(waitTime);
                }
            }
            // Animation off (or failed to start): launch immediately.
            Launch(charge01, charBase);
        }

        /// <summary>Cycles sequentially through the variant library.</summary>
        private ThrowVariant PickNextVariant()
        {
            var lib = ThrowVariantLibrary.All;
            if (lib == null || lib.Count == 0) return null;
            var v = lib[_variantCycleIdx % lib.Count];
            _variantCycleIdx = (_variantCycleIdx + 1) % lib.Count;
            return v;
        }

        /// <summary>
        /// Soft dependency: posts to HHMods.QoL's ChatBox if the plugin is loaded, otherwise
        /// silently no-ops. Uses reflection so we don't have to add a hard project reference.
        /// </summary>
        private static void NotifyChatBox(string message)
        {
            try
            {
                var chatBoxType = System.Type.GetType("HHMods.QoL.ChatBox.ChatBox, HHMods.QoL");
                if (chatBoxType == null) return;
                var postMethod = chatBoxType.GetMethod("Post", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                if (postMethod == null) return;
                var noticeTagType = System.Type.GetType("HHMods.QoL.ChatBox.NoticeTag, HHMods.QoL");
                var infoTag = noticeTagType != null ? System.Enum.ToObject(noticeTagType, 0) : (object)0;
                postMethod.Invoke(null, new object[] { message, infoTag });
            }
            catch { /* soft dep */ }
        }

        private void Launch(float charge01, global::C_Controller_Base charBase)
        {
            var cam = ResolveMainCamera();
            if (cam == null)
            {
                Plugin.Log.LogWarning("[Throw] no main camera resolvable; skipping launch");
                return;
            }

            var camTf = cam.transform;
            // Prefer the player's right-hand transform for spawn so the projectile leaves
            // the actual hand at the peak of the throw animation. Falls back to the camera
            // if we can't resolve it. Direction is always camera-forward so aim isn't tied
            // to bone orientation (which varies per animation frame).
            Vector3 spawnPos;
            var rightHand = ResolveRightHand(charBase);
            if (rightHand != null)
                spawnPos = rightHand.position + camTf.forward * 0.15f;
            else
                spawnPos = camTf.position + camTf.forward * 0.6f + camTf.up * -0.15f;
            var speed = Mathf.Lerp(Plugin.MinThrowForce.Value, Plugin.MaxThrowForce.Value, charge01);

            var go = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            go.name = $"HHMods.Weapons.Projectile[{Time.frameCount}]";
            go.transform.position = spawnPos;
            // Rock look: non-uniform scale + random rotation so it doesn't read as a perfect
            // ball. Still a sphere underneath (collider stays cheap), but the visual is chunky.
            var s = Plugin.ProjectileScale.Value;
            go.transform.localScale = new Vector3(s, s * Random.Range(0.75f, 0.95f), s * Random.Range(0.75f, 0.95f));
            go.transform.rotation = Random.rotationUniform;

            // CreatePrimitive gives us a SphereCollider and MeshRenderer. Keep the collider,
            // give it a Rigidbody + initial velocity.
            var rb = go.AddComponent<Rigidbody>();
            rb.mass = Plugin.ProjectileMass.Value;
            rb.velocity = camTf.forward * speed;
            rb.angularVelocity = Random.insideUnitSphere * 8f;   // spin for visual character
            rb.collisionDetectionMode = CollisionDetectionMode.ContinuousDynamic;
            rb.interpolation = RigidbodyInterpolation.Interpolate;

            // HDRP-safe material. The default primitive material references the built-in
            // Legacy/Standard shader which doesn't compile under HDRP, so the sphere
            // renders as nothing. Build a fresh HDRP/Unlit material and swap it in.
            var mr = go.GetComponent<MeshRenderer>();
            if (mr != null) mr.sharedMaterial = ResolveRockMaterial();

            if (Plugin.DrawTrail.Value)
            {
                var trail = go.AddComponent<TrailRenderer>();
                trail.time = 0.6f;
                trail.startWidth = Plugin.ProjectileScale.Value * 0.9f;
                trail.endWidth = 0f;
                trail.material = ResolveTrailMaterial();
                trail.startColor = new Color(0.85f, 0.82f, 0.75f, 0.65f);   // pale dust
                trail.endColor   = new Color(0.85f, 0.82f, 0.75f, 0f);
                trail.minVertexDistance = 0.05f;
            }

            // Attach a simple collision reporter for diagnostic purposes. Real damage comes
            // in a later pass when we build the mine + grenade explosions on this pipeline.
            go.AddComponent<ProjectileDiagnostic>();

            Object.Destroy(go, Plugin.ProjectileLifetimeSeconds.Value);

            Plugin.Log.LogInfo($"[Throw] launched  charge={charge01:F2}  v={speed:F1} m/s  mass={rb.mass}kg");
        }

        // ---------- Animation resolution ----------

        private static AnimationClip _cachedClip;
        private static string _cachedClipName;

        /// <summary>
        /// Looks up the throw AnimationClip by name from currently-loaded resources. Result
        /// is cached until the config name changes.
        /// </summary>
        private static AnimationClip ResolveThrowClip()
        {
            var name = Plugin.ThrowAnimationClipName.Value;
            if (string.IsNullOrEmpty(name)) return null;
            if (_cachedClip != null && _cachedClipName == name) return _cachedClip;
            foreach (var c in Resources.FindObjectsOfTypeAll<AnimationClip>())
            {
                if (c != null && c.name == name)
                {
                    _cachedClip = c;
                    _cachedClipName = name;
                    Plugin.Log.LogInfo($"[Throw] resolved animation clip '{name}' (len={c.length:F2}s)");
                    return c;
                }
            }
            return null;
        }

        private static global::C_Controller_Base ResolvePlayerCharBase()
        {
            var players = Object.FindObjectsOfType<global::Player_Input>(includeInactive: false);
            if (players == null || players.Length == 0) return null;
            var p = players[0];
            var cb = p.GetComponent<global::C_Controller_Base>();
            if (cb == null) cb = p.GetComponentInParent<global::C_Controller_Base>();
            return cb;
        }

        private static System.Reflection.FieldInfo _fRightHand;

        private static Transform ResolveRightHand(global::C_Controller_Base charBase)
        {
            if (charBase == null) return null;
            // Tool_Interacter (equipped item) exposes _rightHand — private, reach via reflection.
            var ti = charBase.GetComponentInChildren<global::Tool_Interacter>();
            if (ti != null)
            {
                if (_fRightHand == null)
                    _fRightHand = typeof(global::Tool_Interacter).GetField("_rightHand",
                        System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                var t = _fRightHand?.GetValue(ti) as Transform;
                if (t != null) return t;
            }
            // Fallback: find a transform under the char root that names "Hand_R" / "R_Hand" / etc.
            foreach (var t in charBase.GetComponentsInChildren<Transform>(includeInactive: true))
            {
                var n = t.name;
                if (n == null) continue;
                if (n.Equals("Hand_R", System.StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("R_Hand", System.StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("RightHand", System.StringComparison.OrdinalIgnoreCase) ||
                    n.Equals("Right_Hand", System.StringComparison.OrdinalIgnoreCase))
                    return t;
            }
            return null;
        }

        /// <summary>
        /// Plays a clip on the player's Animancer. Prefers the upper-body layer so the legs
        /// keep doing their walk/idle unaffected. Falls back to the base component's Play
        /// call if the layer isn't accessible.
        /// </summary>
        private static void PlayClipOnPlayer(global::C_Controller_Base charBase, AnimationClip clip, float speed)
        {
            var animancer = charBase._3rd_Animancer;
            if (animancer == null) { Plugin.Log.LogWarning("[Throw] no _3rd_Animancer on player"); return; }
            try
            {
                Animancer.AnimancerState state = null;
                if (Plugin.UseUpperBodyLayer.Value && charBase._UpperBodyLayer != null)
                {
                    state = charBase._UpperBodyLayer.Play(clip);
                    charBase._UpperBodyLayer.Weight = 1f;
                }
                else
                {
                    state = animancer.Play(clip);
                }
                if (state != null) state.Speed = speed;
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogWarning($"[Throw] failed to play clip '{clip.name}': {e.Message}");
            }
        }

        private static Camera ResolveMainCamera()
        {
            var cc = MgrHub.Cam;
            if (cc != null && cc._mainCam != null) return cc._mainCam;
            foreach (var c in Object.FindObjectsOfType<Camera>())
                if (c != null && c.gameObject.name == "Main_Camera") return c;
            return Camera.main;
        }

        // ---------- Material resolution (HDRP-safe) ----------

        private static Material _cachedRockMat;
        private static Material _cachedTrailMat;

        /// <summary>
        /// Returns a rocky grey material that actually renders under HDRP. Tries HDRP/Unlit
        /// first (universal, cheap, no lighting dependency), then HDRP/Lit, then falls
        /// through to Sprites/Default which is guaranteed to exist.
        /// </summary>
        private static Material ResolveRockMaterial()
        {
            if (_cachedRockMat != null) return _cachedRockMat;
            var shader = Shader.Find("HDRP/Unlit") ?? Shader.Find("HDRP/Lit") ?? Shader.Find("Sprites/Default");
            if (shader == null)
            {
                Plugin.Log.LogWarning("[Throw] no usable shader found; projectile will be invisible");
                return null;
            }
            var m = new Material(shader);
            m.hideFlags = HideFlags.HideAndDontSave;
            // HDRP materials use "_BaseColor" (Unlit + Lit); Sprites uses "_Color". Set both.
            var rocky = new Color(0.42f, 0.38f, 0.33f);
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", rocky);
            if (m.HasProperty("_Color"))     m.SetColor("_Color", rocky);
            m.name = "HHMods.Weapons.Rock";
            _cachedRockMat = m;
            Plugin.Log.LogInfo($"[Throw] rock material using shader: {shader.name}");
            return m;
        }

        private static Material ResolveTrailMaterial()
        {
            if (_cachedTrailMat != null) return _cachedTrailMat;
            // Additive particle-style trail so it reads regardless of scene lighting.
            var shader = Shader.Find("Legacy Shaders/Particles/Additive")
                      ?? Shader.Find("Particles/Additive")
                      ?? Shader.Find("Sprites/Default")
                      ?? Shader.Find("HDRP/Unlit");
            if (shader == null) return null;
            var m = new Material(shader) { hideFlags = HideFlags.HideAndDontSave, name = "HHMods.Weapons.Trail" };
            _cachedTrailMat = m;
            return m;
        }
    }

    /// <summary>
    /// Logs collision hits so we can verify the projectile is actually striking scene
    /// geometry / NPCs. Purely a diagnostic; no damage or effect is applied. Suppresses
    /// duplicate-per-frame hits (creatures with compound colliders fire OnCollisionEnter
    /// once per body part) so the log stays legible.
    /// </summary>
    internal class ProjectileDiagnostic : MonoBehaviour
    {
        private bool _logged;

        private void OnCollisionEnter(Collision collision)
        {
            if (_logged) return;
            _logged = true;
            var other = collision.gameObject;
            var rb = GetComponent<Rigidbody>();
            var v = rb != null ? rb.velocity.magnitude : 0f;
            Plugin.Log.LogInfo($"[Throw]   hit  target='{other.name}'  layer={LayerMask.LayerToName(other.layer)}  vAtHit={v:F1} m/s");
        }
    }
}
