using CompassNavigatorPro;
using HHMods.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HHMods.Minimap
{
    /// <summary>
    /// Instantiates a PRIVATE copy of the game's CompassPro GameObject and configures
    /// that copy for our HUD minimap. The game's own CompassPro is only referenced (to
    /// clone from) — never mutated. Toggle-off destroys our copy entirely; toggle-on
    /// re-instantiates from the game's compass.
    ///
    /// This is the second-attempt design. The first attempt (mutate the shared instance)
    /// created a running battle with the game's world map, POI system, curtain quads,
    /// fog overlays, and Nature Renderer per-camera settings. That approach is abandoned.
    /// </summary>
    public class MinimapController : MonoBehaviour
    {
        internal static MinimapController Instance { get; private set; }

        private CompassPro _gameCompass;      // reference to game's compass (source for Instantiate)
        private GameObject _ourGO;             // OUR private compass GameObject
        private CompassPro _ourCompass;        // OUR CompassPro component on _ourGO
        private bool _minimapOn;
        private FilterPanel _filterPanel;

        // Resolve state
        private const float ResolveInterval = 2f;
        private float _nextResolveTime;
        private int _resolveAttempts;

        // Zoom-lock state (per our clone) — lock every field that scroll input might touch,
        // because both the game's compass and our clone read Input.mouseScrollDelta in their Update.
        private bool _zoomLocked;
        private float _lockedZoomLevel;
        private float _lockedSize;
        private float _lockedCaptureSize;
        private float _lockedHeightVSFollow;
        private float _lockedPlayerIconSize;
        private Vector3 _lockedUIScale;
        private Vector2 _lockedUISizeDelta;
        private RectTransform _lockedUIRT;
        private RectTransform _lockedPlayerIconRT;
        private Vector3 _lockedPlayerIconScale;

        // Cached minimap camera reference so we can enforce top-down orientation each frame.
        private Camera _cachedMinimapCam;
        // Cached Main_Camera so map orientation follows the camera direction, not the character body.
        private Camera _cachedMainCam;

        // Scoped directional light — enabled only while our minimap camera is rendering,
        // so night doesn't blackout the map but the main game view stays unaffected.
        private Light _minimapLight;
        private UnityEngine.Rendering.HighDefinition.HDAdditionalLightData _minimapLightHD;
        private bool _renderCallbacksRegistered;

        // References we keep so ConfigEntry SettingChanged handlers can mutate them live.
        private UnityEngine.Rendering.HighDefinition.Exposure _liveExposure;
        private UnityEngine.Rendering.HighDefinition.ColorAdjustments _liveColorAdj;
        private int _originalCullingMask = int.MinValue;

        // Our OWN player-icon RectTransform, spawned as a child of MiniMapMask.
        // Bypasses CompassPro's icon-spawning system entirely (which doesn't cleanly repopulate on a clone).
        private RectTransform _customPlayerIconRT;

        // Static "N" marker at the top of the minimap frame (12 o'clock).
        private RectTransform _northMarkerRT;

        // Throttle for the diagnostic yaw log in LateUpdate.
        private float _nextYawLog;
        // Defensive re-apply timer — re-asserts our render settings periodically to counter
        // external systems (Enviro, Nature Renderer, CompassPro's own Update) that reset them.
        private float _nextDefensiveReapply;

        // Captured anchored position of our clone's MiniMap Root — we shift it down each
        // frame to leave room for the HUD bar above the minimap. Store the original once so
        // repeated pushes don't compound.
        private Vector2? _origMiniMapRootAnchoredPos;

        // POI SpriteRenderers we hid during our render pass. Restored in OnEndCameraRender.
        // Preallocate to avoid per-frame GC.
        private readonly System.Collections.Generic.List<SpriteRenderer> _suspendedPOISprites =
            new System.Collections.Generic.List<SpriteRenderer>(32);
        private readonly System.Collections.Generic.List<CompassProPOI> _tmpPOIs =
            new System.Collections.Generic.List<CompassProPOI>(64);

        // True when a scene unload tore down the clone but the minimap was on.
        // BindGameCompass consumes this to auto-re-enable on the new scene's compass.
        private bool _wasEnabledBeforeSceneLoad;

        // Periodic POI sync: mirror POIs from the game compass to our clone so airdrops,
        // beds, merchants etc. render on the minimap. Runs every couple seconds since
        // POIs can be added/destroyed during play (airdrops spawn/despawn).
        private const float POISyncInterval = 2f;
        private float _nextPOISync;

        private void Awake()
        {
            Instance = this;
            SceneManager.sceneLoaded   += OnSceneLoaded;
            SceneManager.sceneUnloaded += OnSceneUnloaded;
        }

        private void OnDestroy()
        {
            if (_ourGO != null) Destroy(_ourGO);
            SceneManager.sceneLoaded   -= OnSceneLoaded;
            SceneManager.sceneUnloaded -= OnSceneUnloaded;
        }

        private void OnSceneUnloaded(Scene scene)
        {
            // Kill the clone BEFORE its next Update fires with stale scene refs.
            // CompassPro's internal POI list holds pointers to scene-scoped POIs that just
            // got destroyed → UpdateIcons NRE. Also frees the RenderTexture so a save-swap
            // doesn't display the previous save's terrain snapshot.
            if (_ourGO == null && !_minimapOn) return;
            Plugin.Log.LogInfo($"[MinimapController] scene UNLOADED: {scene.name} — tearing down clone (was on: {_minimapOn})");
            _wasEnabledBeforeSceneLoad = _minimapOn;
            TryDisableMinimap();
            _gameCompass = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (mode != LoadSceneMode.Single) return;   // ignore additive/chunk streams
            _gameCompass = null;
            _cachedMainCam = null;
            _resolveAttempts = 0;
            _nextResolveTime = 0f;
            Plugin.Log.LogInfo($"[MinimapController] scene loaded (Single): {scene.name} — will resolve new game compass"
                + (_wasEnabledBeforeSceneLoad ? "; will auto-re-enable minimap" : ""));
        }

        // Called from the Harmony postfix on World_Map_Mgr._Start
        internal void OnWorldMapMgrStarted(global::World_Map_Mgr wm)
        {
            if (_gameCompass != null || wm == null) return;
            if (wm._CompassPro != null) BindGameCompass(wm._CompassPro, "Harmony(World_Map_Mgr._Start).postfix");
        }

        private void Update()
        {
            // Belt-and-suspenders: if the sceneUnloaded callback missed anything,
            // detect stale refs here and tear the clone down.
            if (_minimapOn && (_ourCompass == null || _gameCompass == null))
            {
                Plugin.Log.LogInfo("[MinimapController] stale refs detected in Update — tearing down");
                _wasEnabledBeforeSceneLoad = true;
                TryDisableMinimap();
                _gameCompass = null;
            }

            if (_gameCompass == null)
            {
                if (Time.unscaledTime >= _nextResolveTime)
                {
                    _nextResolveTime = Time.unscaledTime + ResolveInterval;
                    TryResolveGameCompass();
                }
                return;
            }
            // Periodic POI sync — keeps our clone's icon list in step with the game compass
            // so newly-spawned airdrops appear and destroyed ones stop dangling.
            if (_minimapOn && _ourCompass != null && _gameCompass != null
                && Time.unscaledTime >= _nextPOISync)
            {
                _nextPOISync = Time.unscaledTime + POISyncInterval;
                SyncPOIsFromGameCompass();
            }
        }

        private void SyncPOIsFromGameCompass()
        {
            try
            {
                var gamePois = new System.Collections.Generic.List<CompassProPOI>();
                _gameCompass.POIGetAll(gamePois);

                int added = 0, skippedPlayer = 0;
                foreach (var poi in gamePois)
                {
                    if (poi == null) continue;
                    // Skip the player's own POI — we render our own center-anchored arrow.
                    // Without this filter, CompassPro spawns a Hunter icon under our mask
                    // in addition to our custom arrow, giving the "duplicate arrows" look.
                    if (IsPlayerPOI(poi)) { skippedPlayer++; continue; }
                    if (!_ourCompass.POIisRegistered(poi))
                    {
                        _ourCompass.POIRegister(poi);
                        added++;
                    }
                }

                // Prune anything on our clone that's no longer on the game compass (destroyed / removed).
                var ourPois = new System.Collections.Generic.List<CompassProPOI>();
                _ourCompass.POIGetAll(ourPois);
                int removed = 0;
                foreach (var poi in ourPois)
                {
                    if (poi == null || !_gameCompass.POIisRegistered(poi) || IsPlayerPOI(poi))
                    {
                        _ourCompass.POIUnregister(poi);
                        removed++;
                    }
                }

                // Cap per-POI icon scaling so a nearby POI doesn't blow up its icon to fill the minimap.
                // Both fields exist on CompassProPOI; setting to 1 means no per-POI multiplier.
                foreach (var poi in ourPois)
                {
                    if (poi == null) continue;
                    poi.iconScale = 1f;
                    poi.miniMapIconScale = 1f;
                }

                // Orphan-icon sweep: remove any arrow-sprite children from MiniMapMask that
                // aren't OUR custom PlayerIcon. This catches the case where CompassPro spawned
                // a Hunter icon under our mask before we started skipping the player POI.
                CleanupMaskArrowIcons();

                if (added > 0 || removed > 0)
                    Plugin.Log.LogInfo($"[POI sync] +{added} -{removed}   (game total: {gamePois.Count}, skipped-player: {skippedPlayer})");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[POI sync] threw: {e}"); }
        }

        /// <summary>
        /// Destroys any Image on MiniMapMask whose sprite matches the player-arrow sprite
        /// unless it's our own custom icon. Runs each POI sync to clean up leftover icons.
        /// </summary>
        private void CleanupMaskArrowIcons()
        {
            try
            {
                var maskT = GetCloneMiniMapMask();
                if (maskT == null) return;

                // Sanity: verify the mask is actually under OUR clone before destroying anything.
                bool isOurs = false;
                for (var p = maskT; p != null; p = p.parent)
                    if (p.gameObject == _ourGO) { isOurs = true; break; }
                if (!isOurs)
                {
                    Plugin.Log.LogError("[MinimapController] ABORT CleanupMaskArrowIcons: mask isn't under our clone. Skipped so we don't destroy game-side UI.");
                    return;
                }

                int killed = 0;
                foreach (var img in maskT.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                {
                    if (img == null) continue;
                    if (img.gameObject.name == "HHMods.PlayerIcon") continue;
                    var spr = img.sprite;
                    if (spr != null && spr.name == "icon-arrow-white")
                    {
                        Destroy(img.gameObject);
                        killed++;
                    }
                }
                if (killed > 0) Plugin.Log.LogInfo($"[MinimapController] destroyed {killed} orphan arrow icon(s) on mask");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] CleanupMaskArrowIcons threw: {e}"); }
        }

        /// <summary>
        /// True if this POI is attached to (or a child of) the player character. We use several
        /// checks because CompassProPOI can sit on any GameObject and naming isn't consistent.
        /// </summary>
        private static bool IsPlayerPOI(CompassProPOI poi)
        {
            if (poi == null || poi.gameObject == null) return false;
            var go = poi.gameObject;

            // Strongest signal: the POI is on (or under) a Player_Input.
            if (go.GetComponentInParent<global::Player_Input>() != null) return true;

            // Sprite-based signal: this game's player POI uses "icon-arrow-white".
            // Any POI carrying that sprite is the arrow-style player marker.
            if (poi.iconNonVisited != null && poi.iconNonVisited.name == "icon-arrow-white") return true;
            if (poi.iconVisited != null    && poi.iconVisited.name    == "icon-arrow-white") return true;

            // Name-based fallback.
            var n = go.name ?? "";
            if (n.IndexOf("Hunter", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (n.IndexOf("Player", System.StringComparison.OrdinalIgnoreCase) >= 0) return true;

            return false;
        }

        private void LateUpdate()
        {
            if (!_zoomLocked || !_minimapOn || _ourCompass == null) return;

            // ---- Force top-down camera orientation ----
            // CompassPro's built-in tilt/rotation gives us a perspective 3D view from behind
            // the character. That's not what a HUD minimap should be. Override the camera
            // transform directly here — LateUpdate runs after CompassPro's Update, so we win.
            if (_cachedMinimapCam == null)
                _cachedMinimapCam = GetCloneCamera();   // safe: guaranteed to be our clone's camera
            // Re-resolve every frame in case the game swapped cameras (e.g. entering a vehicle).
            _cachedMainCam = ResolveMainCamera();

            var follow = _ourCompass.miniMapFollow;   // position tracks character body (correct)
            if (_cachedMinimapCam != null && follow != null)
            {
                var height = _ourCompass.miniMapCameraHeightVSFollow;

                // Yaw from projected forward, not eulerAngles, so pitch doesn't corrupt it.
                var cameraYaw = _cachedMainCam != null ? HorizontalYawFrom(_cachedMainCam.transform) : HorizontalYawFrom(follow);
                var bodyYaw   = HorizontalYawFrom(follow);

                // ---- MAP: locked north-up, always ----
                _cachedMinimapCam.transform.position = follow.position + Vector3.up * height;
                _cachedMinimapCam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

                // ---- ICON: rotates to show camera cardinal direction ----
                // The icon-arrow-white sprite is natively oriented pointing DOWN (verified in-game:
                // yaw=0 rendered arrow pointing south with the previous -yaw formula). So we add
                // 180° to align the visual with the intended semantics: camera facing north → up,
                // east → right, south → down, west → left.
                if (_customPlayerIconRT != null)
                    _customPlayerIconRT.localRotation = Quaternion.Euler(0f, 0f, 180f - cameraYaw);

                // Throttled diagnostic — prints every 3s so we can confirm camYaw actually
                // tracks the camera (not the body) after the CamController wiring change.
                if (Time.unscaledTime >= _nextYawLog)
                {
                    _nextYawLog = Time.unscaledTime + 3f;
                    var camName = _cachedMainCam != null ? _cachedMainCam.gameObject.name : "(null)";
                    Plugin.Log.LogInfo($"[LateUpdate] mainCam='{camName}' camYaw={cameraYaw:0.0}  bodyYaw={bodyYaw:0.0}");
                }
            }

            // Defensive re-apply — external systems (Enviro, Nature Renderer, CompassPro's
            // Update) can reset our exposure / saturation / culling mid-session and cause
            // the minimap to blank out or overexpose after a second or two. Re-asserting
            // once per second is cheap and keeps our tuning sticky.
            if (Time.unscaledTime >= _nextDefensiveReapply)
            {
                _nextDefensiveReapply = Time.unscaledTime + 1f;
                DefensiveReapply();
            }

            // Icon sizing must be re-capped EVERY FRAME — CompassPro's own LateUpdate scales
            // icons per-frame based on distance, and the 1Hz DefensiveReapply is too slow
            // to prevent visible flicker between "correct" and "huge" states.
            CapMinimapIconSizes();

            // Force every scroll-touchable value back to its locked snapshot each frame.
            // Direct assignment (not conditional) — cheaper than the approx checks and impossible to miss.
            _ourCompass.miniMapZoomLevel = _lockedZoomLevel;
            _ourCompass.miniMapSize = _lockedSize;
            _ourCompass.miniMapCaptureSize = _lockedCaptureSize;
            _ourCompass.miniMapCameraHeightVSFollow = _lockedHeightVSFollow;
            _ourCompass.miniMapPlayerIconSize = _lockedPlayerIconSize;

            // Lock the MiniMap RectTransform's localScale + sizeDelta
            if (_lockedUIRT == null)
            {
                var img = GetField<UnityEngine.UI.Image>(_ourCompass, "miniMapImage");
                if (img != null) { _lockedUIRT = img.rectTransform; _lockedUIScale = _lockedUIRT.localScale; _lockedUISizeDelta = _lockedUIRT.sizeDelta; }
            }
            if (_lockedUIRT != null)
            {
                _lockedUIRT.localScale = _lockedUIScale;
                _lockedUIRT.sizeDelta = _lockedUISizeDelta;
            }

            // Lock the player-icon RectTransform if we've resolved it
            if (_lockedPlayerIconRT != null)
                _lockedPlayerIconRT.localScale = _lockedPlayerIconScale;
        }

        private void TryResolveGameCompass()
        {
            _resolveAttempts++;
            var wm = global::World_Map_Mgr.ins;
            if (wm != null && wm._CompassPro != null) { BindGameCompass(wm._CompassPro, "World_Map_Mgr.ins._CompassPro"); return; }
            var found = Object.FindObjectsOfType<CompassPro>(includeInactive: true);
            if (found != null && found.Length > 0) { BindGameCompass(found[0], $"FindObjectsOfType (found {found.Length})"); return; }

            if (_resolveAttempts == 1 || _resolveAttempts % 15 == 0)
                Plugin.Log.LogInfo($"[MinimapController] resolve attempt #{_resolveAttempts} — no CompassPro yet.");
        }

        private void BindGameCompass(CompassPro gameCompass, string source)
        {
            _gameCompass = gameCompass;
            Plugin.Log.LogInfo($"[MinimapController] game CompassPro bound (source: {source}). We will Instantiate() our own copy on enable.");
            _filterPanel = gameObject.GetComponent<FilterPanel>() ?? gameObject.AddComponent<FilterPanel>();
            if (Plugin.ShowOnStart.Value || _wasEnabledBeforeSceneLoad)
            {
                _wasEnabledBeforeSceneLoad = false;
                TryEnableMinimap();
            }
        }

        // ---------- Enable / Disable (destroy-and-recreate model) ----------

        internal void SetVisible(bool visible)
        {
            if (visible) TryEnableMinimap(); else TryDisableMinimap();
        }

        internal bool IsMinimapOn => _minimapOn;

        private void TryEnableMinimap()
        {
            if (_gameCompass == null || _minimapOn) return;

            // Instantiate the game's CompassPro GameObject verbatim — we get a full private copy
            // (Camera, UI hierarchy, materials, etc.) that we can configure freely.
            _ourGO = Instantiate(_gameCompass.gameObject);
            _ourGO.name = "HHMods.Minimap.CompassPro";
            DontDestroyOnLoad(_ourGO);
            _ourCompass = _ourGO.GetComponent<CompassPro>();

            if (_ourCompass == null)
            {
                Plugin.Log.LogError("[MinimapController] Instantiate failed to yield a CompassPro on the clone");
                Destroy(_ourGO); _ourGO = null;
                return;
            }

            // Hide the compass bar on our copy — the game's own is already showing one and we
            // don't want two. Minimap alpha stays separate.
            _ourCompass.alpha = 0f;

            ApplyView(0);   // single preset — always index 0
            EnsureHDRPOnClone();
            HideCurtainsOnClone();
            NeuterAutoAddedCameraComponents();

            // Independent from CompassPro's icon system — a simple center-mounted arrow
            // we can always control.
            SpawnCustomPlayerIcon();
            SpawnNorthMarker();

            // Filter the minimap camera's cullingMask so we don't render UI/effects layers
            // (CompassPro spawns compass-bar icons as world-space sprites at POI positions;
            // our top-down camera captures those huge if we don't filter them out).
            FilterMinimapCameraCulling();

            // Add a scoped directional light for the minimap camera. Toggled per-camera via
            // render pipeline callbacks so it doesn't affect the main scene.
            SetupMinimapDirectionalLight();
            RegisterRenderCallbacks();

            _minimapOn = true;
            Plugin.Log.LogInfo($"[MinimapController] HUD minimap ENABLED on PRIVATE CompassPro clone ({_ourGO.name})");
            LogCloneState();
        }

        private void TryDisableMinimap()
        {
            if (!_minimapOn && _ourGO == null) return;

            _zoomLocked = false;
            _lockedUIRT = null;

            UnregisterRenderCallbacks();

            if (_ourGO != null)
            {
                Destroy(_ourGO);
                _ourGO = null;
                _ourCompass = null;
                _cachedMinimapCam = null;
                _minimapLight = null;
                _customPlayerIconRT = null;
                _northMarkerRT = null;
                Plugin.Log.LogInfo("[MinimapController] destroyed private CompassPro clone");
            }
            _minimapOn = false;
        }

        // ---------- Safe clone-camera resolution ----------

        /// <summary>
        /// Returns the minimap camera that belongs to OUR clone, guaranteed. Walks the clone's
        /// hierarchy for a Camera component. Never returns the game's camera even if the
        /// serialized <c>miniMapCamera</c> field on <c>_ourCompass</c> is still aliasing the
        /// game object (which happens when Instantiate doesn't remap runtime-set references).
        /// </summary>
        private Camera GetCloneCamera()
        {
            if (_ourGO == null) return null;
            return _ourGO.GetComponentInChildren<Camera>(includeInactive: true);
        }

        /// <summary>
        /// Returns OUR clone's "MiniMap Root" transform. Same aliasing risk as the camera —
        /// reflecting <c>_ourCompass.miniMapUIRoot</c> can hand back the GAME's UI root
        /// when Instantiate hasn't remapped the reference, which would cause us to spawn
        /// our icons on the game's mask and destroy game-side icons during cleanup.
        /// </summary>
        private Transform GetCloneUIRoot()
        {
            if (_ourGO == null) return null;
            // MiniMap Root is a known child in the CompassPro hierarchy.
            foreach (var t in _ourGO.GetComponentsInChildren<Transform>(includeInactive: true))
                if (t.name == "MiniMap Root") return t;
            // Fallback: walk to the top of the mask's parent chain.
            var mask = GetCloneMiniMapMask();
            return mask != null ? mask.parent?.parent : null;
        }

        /// <summary>Returns OUR clone's MiniMapMask transform.</summary>
        private Transform GetCloneMiniMapMask()
        {
            if (_ourGO == null) return null;
            foreach (var t in _ourGO.GetComponentsInChildren<Transform>(includeInactive: true))
                if (t.name == "MiniMapMask") return t;
            return null;
        }

        // ---------- Main camera resolution ----------

        private Camera ResolveMainCamera()
        {
            // Authoritative: Mgr_Hub._CamController._mainCam — the game exposes it directly.
            var cc = MgrHub.Cam;
            if (cc != null && cc._mainCam != null) return cc._mainCam;
            // Fallback A: name lookup
            foreach (var c in Object.FindObjectsOfType<Camera>())
                if (c != null && c.gameObject.name == "Main_Camera") return c;
            // Fallback B: Unity's default
            return Camera.main;
        }

        /// <summary>Yaw of the given transform's forward direction, projected onto the
        /// horizontal plane. Avoids the eulerAngles-aliases-when-pitched problem.</summary>
        private static float HorizontalYawFrom(Transform t)
        {
            if (t == null) return 0f;
            var f = t.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) return t.eulerAngles.y; // camera pointing straight up/down
            f.Normalize();
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        // ---------- Custom center-mounted player icon ----------

        private void SpawnCustomPlayerIcon()
        {
            try
            {
                if (_customPlayerIconRT != null) return;

                var uiRoot = GetCloneUIRoot();
                if (uiRoot == null) { Plugin.Log.LogWarning("[MinimapController] SpawnCustomPlayerIcon: miniMapUIRoot null"); return; }

                var maskT = uiRoot.Find("MiniMap/MiniMapMask");
                if (maskT == null)
                {
                    // Fallback: any descendant named MiniMapMask.
                    foreach (var t in uiRoot.GetComponentsInChildren<Transform>(true))
                        if (t.name == "MiniMapMask") { maskT = t; break; }
                }
                if (maskT == null) { Plugin.Log.LogWarning("[MinimapController] SpawnCustomPlayerIcon: MiniMapMask not found"); return; }

                // Reuse the game's arrow sprite. Prefer 'icon-arrow-white' since it already ships,
                // fall back to any arrow-named sprite in the clone hierarchy.
                Sprite arrowSprite = null;
                foreach (var img in _ourGO.GetComponentsInChildren<UnityEngine.UI.Image>(true))
                {
                    var s = img.sprite;
                    if (s == null) continue;
                    if (s.name == "icon-arrow-white") { arrowSprite = s; break; }
                    if (arrowSprite == null && s.name.IndexOf("arrow", System.StringComparison.OrdinalIgnoreCase) >= 0)
                        arrowSprite = s;
                }

                var iconGO = new GameObject("HHMods.PlayerIcon");
                var rt = iconGO.AddComponent<RectTransform>();
                rt.SetParent(maskT, false);
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = Vector2.zero;
                rt.sizeDelta = Vector2.one * Plugin.ArrowSize.Value;
                rt.localScale = Vector3.one;
                rt.localRotation = Quaternion.identity;

                var img2 = iconGO.AddComponent<UnityEngine.UI.Image>();
                img2.raycastTarget = false;
                img2.color = new Color(0f, 0f, 0f, Plugin.ArrowFillAlpha.Value);
                if (arrowSprite != null) img2.sprite = arrowSprite;

                // Drop-shadow via UI.Shadow (single offset, not Outline's 4-corner multi-copy).
                var shadow = iconGO.AddComponent<UnityEngine.UI.Shadow>();
                shadow.effectColor = new Color(0f, 0f, 0f, Plugin.ArrowOutlineAlpha.Value);
                shadow.effectDistance = new Vector2(Plugin.ArrowOutlineDistance.Value, -Plugin.ArrowOutlineDistance.Value);
                shadow.useGraphicAlpha = false;

                _customPlayerIconRT = rt;
                Plugin.Log.LogInfo($"[MinimapController] spawned custom player icon under MiniMapMask (sprite={(arrowSprite != null ? arrowSprite.name : "(none)")}");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] SpawnCustomPlayerIcon threw: {e}"); }
        }

        // ---------- Static "N" marker at 12 o'clock on the frame ----------

        private void SpawnNorthMarker()
        {
            try
            {
                if (_northMarkerRT != null) return;

                var uiRoot = GetCloneUIRoot();
                if (uiRoot == null) { Plugin.Log.LogWarning("[MinimapController] SpawnNorthMarker: miniMapUIRoot null"); return; }

                // Parent to MiniMap Root (uiRoot itself, or its "MiniMap" child if that's the visible frame).
                // uiRoot is the top of the minimap UI hierarchy; anchoring to it means the N sits at
                // the top edge of the whole minimap in screen space, regardless of anchor drift.
                var parent = uiRoot;

                // Steal a font from any existing Text in the hierarchy so we don't have to bundle one.
                Font font = null;
                foreach (var t in _ourGO.GetComponentsInChildren<UnityEngine.UI.Text>(true))
                    if (t.font != null) { font = t.font; break; }

                var markerGO = new GameObject("HHMods.NorthMarker");
                var rt = markerGO.AddComponent<RectTransform>();
                rt.SetParent(parent, false);
                rt.anchorMin = new Vector2(0.5f, 1f);   // top-center of parent
                rt.anchorMax = new Vector2(0.5f, 1f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = new Vector2(0f, 4f);   // 4px above top edge, sits on frame ring
                rt.sizeDelta = new Vector2(18f, 18f);
                rt.localScale = Vector3.one;
                rt.localRotation = Quaternion.identity;

                var text = markerGO.AddComponent<UnityEngine.UI.Text>();
                text.text = "N";
                text.fontSize = 14;
                text.fontStyle = FontStyle.Bold;
                text.alignment = TextAnchor.MiddleCenter;
                text.color = new Color(1f, 0.85f, 0.30f, 1f);   // amber, distinct from white player arrow
                text.raycastTarget = false;
                if (font != null) text.font = font;

                _northMarkerRT = rt;
                Plugin.Log.LogInfo($"[MinimapController] spawned north marker (font={(font != null ? font.name : "(default)")})");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] SpawnNorthMarker threw: {e}"); }
        }

        // ---------- Culling filter ----------

        private void FilterMinimapCameraCulling()
        {
            try
            {
                var cam = GetCloneCamera();   // walks our clone's hierarchy — never returns the game's camera
                if (cam == null) { Plugin.Log.LogWarning("[MinimapController] FilterMinimapCameraCulling: no clone camera"); return; }

                // Capture the original once so live-toggle can re-include a layer without
                // needing to know what it was before we started filtering.
                if (_originalCullingMask == int.MinValue) _originalCullingMask = cam.cullingMask;
                var before = _originalCullingMask;

                int mask = before;
                if (Plugin.CullTransparentFX.Value) mask &= ~(1 << 1);
                if (Plugin.CullWater.Value)         mask &= ~(1 << 4);
                if (Plugin.CullUI.Value)            mask &= ~(1 << 5);

                cam.cullingMask = mask;

                var dropped = new System.Text.StringBuilder();
                for (int i = 0; i < 32; i++)
                {
                    var bit = 1 << i;
                    if ((before & bit) != 0 && (mask & bit) == 0)
                    {
                        var name = LayerMask.LayerToName(i);
                        if (dropped.Length > 0) dropped.Append(", ");
                        dropped.Append($"{i}:'{name}'");
                    }
                }
                Plugin.Log.LogInfo($"[MinimapController] minimap cullingMask 0x{before:X8} → 0x{mask:X8}   dropped=[{dropped}]   ourCamID={cam.GetInstanceID()}");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] FilterMinimapCameraCulling threw: {e}"); }
        }

        // ---------- Scoped directional light (night boost) ----------

        private void SetupMinimapDirectionalLight()
        {
            try
            {
                if (_minimapLight != null) return;

                var lightGO = new GameObject("HHMods.MinimapDirectionalLight");
                lightGO.transform.SetParent(_ourGO.transform, false);
                lightGO.transform.rotation = Quaternion.Euler(Plugin.LightAngleX.Value, Plugin.LightAngleY.Value, 0f);

                var light = lightGO.AddComponent<Light>();
                light.type = LightType.Directional;
                light.shadows = LightShadows.None;
                light.color = Color.white;
                light.intensity = 3f;   // legacy fallback (some HDRP versions read this too)

                try
                {
                    _minimapLightHD = lightGO.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
                    _minimapLightHD.EnableShadows(false);
                    // Apply intensity via the shared helper (uses SetIntensity if available).
                    ApplyLightIntensityToHD(_minimapLightHD, Plugin.LightIntensityLux.Value);
                }
                catch (System.Exception e) { Plugin.Log.LogWarning($"[MinimapController] HDAdditionalLightData setup: {e.Message}"); }

                _minimapLight = light;
                lightGO.SetActive(false);   // gated on/off by render callbacks
                Plugin.Log.LogInfo($"[MinimapController] scoped directional light created ({Plugin.LightIntensityLux.Value:0} lux, pitch={Plugin.LightAngleX.Value:0}°, yaw={Plugin.LightAngleY.Value:0}°)");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] SetupMinimapDirectionalLight threw: {e}"); }
        }

        private static void ApplyLightIntensityToHD(UnityEngine.Rendering.HighDefinition.HDAdditionalLightData hd, float lux)
        {
            if (hd == null) return;
            var setIntensityMI = typeof(UnityEngine.Rendering.HighDefinition.HDAdditionalLightData)
                .GetMethod("SetIntensity", new[] { typeof(float), typeof(UnityEngine.Rendering.HighDefinition.LightUnit) });
            if (setIntensityMI != null)
                setIntensityMI.Invoke(hd, new object[] { lux, UnityEngine.Rendering.HighDefinition.LightUnit.Lux });
            else
            {
                hd.intensity = lux;
                hd.lightUnit = UnityEngine.Rendering.HighDefinition.LightUnit.Lux;
            }
        }

        private void RegisterRenderCallbacks()
        {
            if (_renderCallbacksRegistered) return;
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering += OnBeginCameraRender;
            UnityEngine.Rendering.RenderPipelineManager.endCameraRendering   += OnEndCameraRender;
            _renderCallbacksRegistered = true;
        }

        private void UnregisterRenderCallbacks()
        {
            if (!_renderCallbacksRegistered) return;
            UnityEngine.Rendering.RenderPipelineManager.beginCameraRendering -= OnBeginCameraRender;
            UnityEngine.Rendering.RenderPipelineManager.endCameraRendering   -= OnEndCameraRender;
            _renderCallbacksRegistered = false;
        }

        // Throttle diagnostic logs so they don't flood every frame.
        private float _nextRenderCallbackLog;
        private int _renderCallbackFrames;

        private void OnBeginCameraRender(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _cachedMinimapCam) return;
            _renderCallbackFrames++;

            // Turn our custom light ON via SetActive so HDAdditionalLightData registers with HDRP.
            if (_minimapLight != null && _minimapLight.gameObject != null && !_minimapLight.gameObject.activeSelf)
                _minimapLight.gameObject.SetActive(true);

            // Prevent CompassPro from auto-focusing/zooming the minimap camera onto the nearest POI.
            if (_ourCompass != null) { try { _ourCompass.POIBlur(); } catch { } }

            // Cap icon sizes RIGHT before render — CompassPro may have re-scaled between our
            // LateUpdate and the render pass. This is our last chance to shrink them.
            CapMinimapIconSizes();

            // Suspend every POI's world-space SpriteRenderer for the duration of OUR render.
            _suspendedPOISprites.Clear();
            SuspendPOISpritesFromCompass(_ourCompass);
            SuspendPOISpritesFromCompass(_gameCompass);

            if (Time.unscaledTime >= _nextRenderCallbackLog)
            {
                _nextRenderCallbackLog = Time.unscaledTime + 5f;
                Plugin.Log.LogInfo($"[render callback] BEGIN — ourLight={_minimapLight?.gameObject?.activeSelf}, suspendedPOISprites={_suspendedPOISprites.Count}, frames={_renderCallbackFrames}");
                _renderCallbackFrames = 0;
            }
        }

        private void OnEndCameraRender(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _cachedMinimapCam) return;

            if (_minimapLight != null && _minimapLight.gameObject != null && _minimapLight.gameObject.activeSelf)
                _minimapLight.gameObject.SetActive(false);

            // Restore POI sprite renderers so the game's main-camera view sees them normally.
            for (int i = 0; i < _suspendedPOISprites.Count; i++)
            {
                var sr = _suspendedPOISprites[i];
                if (sr != null) sr.enabled = true;
            }
            _suspendedPOISprites.Clear();
        }

        private void SuspendPOISpritesFromCompass(CompassPro compass)
        {
            if (compass == null) return;
            _tmpPOIs.Clear();
            try { compass.POIGetAll(_tmpPOIs); } catch { return; }
            for (int i = 0; i < _tmpPOIs.Count; i++)
            {
                var poi = _tmpPOIs[i];
                if (poi == null) continue;
                var sr = poi.spriteRenderer;
                if (sr != null && sr.enabled)
                {
                    sr.enabled = false;
                    _suspendedPOISprites.Add(sr);
                }
            }
        }

        // ---------- Live-apply hooks (called from Plugin.SettingChanged handlers) ----------

        internal void ApplyLightIntensityLive()
        {
            if (_minimapLightHD == null) return;
            ApplyLightIntensityToHD(_minimapLightHD, Plugin.LightIntensityLux.Value);
        }

        internal void ApplyLightAngleLive()
        {
            if (_minimapLight == null || _minimapLight.transform == null) return;
            _minimapLight.transform.rotation = Quaternion.Euler(Plugin.LightAngleX.Value, Plugin.LightAngleY.Value, 0f);
        }

        internal void ApplyExposureLive()
        {
            if (_liveExposure == null) return;
            _liveExposure.fixedExposure.value = Plugin.FixedExposureEV.Value;
        }

        internal void ApplySaturationLive()
        {
            if (_liveColorAdj == null) return;
            _liveColorAdj.saturation.value = Plugin.Saturation.Value;
        }

        internal void ApplyArrowStyleLive()
        {
            if (_customPlayerIconRT == null) return;
            _customPlayerIconRT.sizeDelta = Vector2.one * Plugin.ArrowSize.Value;
            var img = _customPlayerIconRT.GetComponent<UnityEngine.UI.Image>();
            if (img != null) img.color = new Color(0f, 0f, 0f, Plugin.ArrowFillAlpha.Value);
            var shadow = _customPlayerIconRT.GetComponent<UnityEngine.UI.Shadow>();
            if (shadow != null)
            {
                shadow.effectDistance = new Vector2(Plugin.ArrowOutlineDistance.Value, -Plugin.ArrowOutlineDistance.Value);
                shadow.effectColor = new Color(0f, 0f, 0f, Plugin.ArrowOutlineAlpha.Value);
            }
        }

        internal void ApplyCullMaskLive()
        {
            FilterMinimapCameraCulling();
        }

        internal void ApplyZoomLive()
        {
            if (_ourCompass == null) return;
            var z = Plugin.ZoomCaptureSize.Value;
            _ourCompass.miniMapCaptureSize = z;
            _ourCompass.miniMapCameraHeightVSFollow = Mathf.Max(150f, z);
            _ourCompass.visibleDistance = Mathf.Max(_ourCompass.visibleDistance, z * 4f);
        }

        /// <summary>World position of the transform the minimap is following (the player).</summary>
        public Vector3 CurrentFollowPosition
        {
            get
            {
                var f = _ourCompass?.miniMapFollow;
                return f != null ? f.position : Vector3.zero;
            }
        }

        /// <summary>Re-asserts every tunable render setting. Called once/second from LateUpdate
        /// to counter external systems that reset our overrides.</summary>
        private void DefensiveReapply()
        {
            ApplyExposureLive();
            ApplySaturationLive();
            ApplyLightIntensityLive();
            ApplyLightAngleLive();
            ApplyArrowStyleLive();
            ApplyCullMaskLive();
            NeuterAutoAddedCameraComponents();   // re-disables any auto-added Nature/GPUI components that reattached
            PushMinimapDownForHUDBar();          // CompassPro sometimes resets the RectTransform position
            HidePlayerPosOverlay();              // CompassPro may re-enable PlayerPos on Update
            CapMinimapIconSizes();               // stop CompassPro's per-frame icon scaling from blowing icons up
        }

        // Throttle for the "big icon detected" diagnostic so it doesn't spam.
        private float _nextBigIconLog;

        /// <summary>
        /// Force each POI's minimap-icon RectTransform to a fixed small size AND permanently
        /// hide each POI's compass-bar icon on our clone (we don't render a compass bar).
        /// Called every frame from LateUpdate + OnBeginCameraRender.
        /// </summary>
        private void CapMinimapIconSizes()
        {
            try
            {
                if (_ourCompass == null) return;
                _tmpPOIs.Clear();
                try { _ourCompass.POIGetAll(_tmpPOIs); } catch { return; }
                bool logBig = Time.unscaledTime >= _nextBigIconLog;
                for (int i = 0; i < _tmpPOIs.Count; i++)
                {
                    var poi = _tmpPOIs[i];
                    if (poi == null) continue;

                    // MiniMap icon — the UI element that should appear as a small dot on the map
                    var miniGO = poi.miniMapIconGameObject;
                    if (miniGO != null)
                    {
                        var rt = miniGO.transform as RectTransform;
                        if (rt != null)
                        {
                            if (logBig && (rt.sizeDelta.x > 40f || rt.localScale.x > 2f))
                            {
                                Plugin.Log.LogInfo($"[CapIcons] BIG icon poi='{poi.name}' before: sizeDelta={rt.sizeDelta}  localScale={rt.localScale}");
                                _nextBigIconLog = Time.unscaledTime + 3f;   // throttle after any hit
                            }
                            rt.sizeDelta = new Vector2(16f, 16f);
                            rt.localScale = Vector3.one;
                        }
                    }

                    // Compass-bar icon — we don't show a compass bar on our clone (alpha=0),
                    // but CompassPro still positions these in world space per frame. Some
                    // Canvas configurations render world-space RectTransforms visible to any
                    // camera looking through them, which our top-down camera very much does.
                    // Permanent SetActive(false); cheap idempotent no-op after first hit.
                    var compassGO = poi.compassIconGameObject;
                    if (compassGO != null && compassGO.activeSelf) compassGO.SetActive(false);
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] CapMinimapIconSizes threw: {e}"); }
        }

        // ---------- View config ----------

        private void ApplyView(int index)
        {
            if (_ourCompass == null) return;
            if (index < 0 || index >= ViewPresets.All.Length) index = 0;
            var p = ViewPresets.All[index];

            var follow = ResolveFollowTransform();
            _ourCompass.miniMapLocation = MINIMAP_LOCATION.TopRight;
            _ourCompass.miniMapPositionAndSize = MINIMAP_POSITION_AND_SIZE.ControlledByCompassNavigatorPro;
            _ourCompass.miniMapSize = Mathf.Clamp01(Plugin.Size.Value / Screen.height);
            _ourCompass.miniMapKeepStraight = true;   // locked north-up — our LateUpdate enforces this regardless
            _ourCompass.miniMapAlpha = 1f;

            // Visible range must be at least the capture radius, otherwise POIs at the edge
            // of what the camera sees are filtered out by CompassPro and never rendered as icons.
            _ourCompass.visibleDistance = Mathf.Max(_ourCompass.visibleDistance, p.CaptureSize * 4f);
            _ourCompass.miniMapIconSize = 20f;
            _ourCompass.miniMapPlayerIconSize = 0f;   // hide CompassPro's own player icon — we render our own custom arrow

            // Cap CompassPro's global icon size fields (private) — prevents per-POI distance
            // scaling from blowing icons up to fill the minimap when the player walks close.
            var maxF = HarmonyLib.AccessTools.Field(typeof(CompassPro), "_maxIconSize");
            var minF = HarmonyLib.AccessTools.Field(typeof(CompassPro), "_minIconSize");
            if (maxF != null) maxF.SetValue(_ourCompass, 30f);
            if (minF != null) minF.SetValue(_ourCompass, 10f);
            // ZoomCaptureSize (ConfigEntry, tunable at runtime) overrides the preset value.
            var zoom = Plugin.ZoomCaptureSize.Value;
            _ourCompass.miniMapCameraHeightVSFollow = Mathf.Max(150f, zoom);
            _ourCompass.miniMapCaptureSize = zoom;
            _ourCompass.miniMapCameraMinAltitude = p.MinAltitude;
            _ourCompass.miniMapCameraMaxAltitude = p.MaxAltitude;
            _ourCompass.miniMapCameraMode = p.CameraMode;
            _ourCompass.miniMapCameraTilt = p.CameraTilt;
            _ourCompass.miniMapStyle = p.Style;
            if (follow != null) _ourCompass.miniMapFollow = follow;
            _ourCompass.showMiniMap = true;

            InvokeSetupMiniMap();

            // Snapshot every scroll-touchable value AFTER SetupMiniMap so we capture the
            // fully-initialized state.
            _lockedZoomLevel = _ourCompass.miniMapZoomLevel;
            _lockedSize = _ourCompass.miniMapSize;
            _lockedCaptureSize = _ourCompass.miniMapCaptureSize;
            _lockedHeightVSFollow = _ourCompass.miniMapCameraHeightVSFollow;
            _lockedPlayerIconSize = _ourCompass.miniMapPlayerIconSize;
            _lockedUIRT = null;
            _lockedPlayerIconRT = null;
            _zoomLocked = true;

            // Ensure our custom UI elements exist (safety net for view cycles
            // — if CompassPro's SetupMiniMap tore them out, we re-spawn).
            SpawnCustomPlayerIcon();
            SpawnNorthMarker();
            HidePlayerPosOverlay();
            PushMinimapDownForHUDBar();
        }

        /// <summary>
        /// Shifts our clone's MiniMap Root RectTransform downward so it clears the HUD bar
        /// rendered above it. Idempotent — always computes offset from the captured original.
        /// </summary>
        private void PushMinimapDownForHUDBar()
        {
            try
            {
                var uiRoot = GetCloneUIRoot();
                if (uiRoot == null) return;
                var rt = uiRoot as RectTransform;
                if (rt == null) return;

                if (!_origMiniMapRootAnchoredPos.HasValue)
                    _origMiniMapRootAnchoredPos = rt.anchoredPosition;

                // HUD bar: 28h + 8 top margin + 8 gap below = 44px total.
                // Bar is top-anchored, so we push the minimap down by pushDown (anchoredPosition.y
                // is negative for downward when the RT itself is top-anchored).
                const float pushDown = 44f;
                var orig = _origMiniMapRootAnchoredPos.Value;
                rt.anchoredPosition = new Vector2(orig.x, orig.y - pushDown);
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] PushMinimapDownForHUDBar threw: {e}"); }
        }

        /// <summary>
        /// Disable CompassPro's built-in PlayerPos coordinate overlay on our clone.
        /// It renders pale grey text on top of the map (hard to read), and we're going to
        /// show a proper high-contrast coordinate readout in the HUD bar above the minimap.
        /// </summary>
        private void HidePlayerPosOverlay()
        {
            try
            {
                var uiRoot = GetCloneUIRoot();
                if (uiRoot == null) return;
                int hidden = 0;
                foreach (var t in uiRoot.GetComponentsInChildren<Transform>(includeInactive: true))
                {
                    if (t.name == "PlayerPos")
                    {
                        if (t.gameObject.activeSelf) t.gameObject.SetActive(false);
                        hidden++;
                    }
                }
                if (hidden > 0) Plugin.Log.LogInfo($"[MinimapController] hid {hidden} PlayerPos overlay(s) on clone");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] HidePlayerPosOverlay threw: {e}"); }
        }

        private Transform ResolveFollowTransform()
        {
            var players = Object.FindObjectsOfType<global::Player_Input>(includeInactive: false);
            if (players != null && players.Length > 0) return players[0].transform;
            var cam = Camera.main;
            return cam != null ? cam.transform : null;
        }

        // ---------- HDRP setup on our clone ----------

        private void EnsureHDRPOnClone()
        {
            try
            {
                var cam = GetCloneCamera();
                var tex = GetField<RenderTexture>(_ourCompass, "miniMapTex");
                if (cam == null) { Plugin.Log.LogWarning("[MinimapController] EnsureHDRPOnClone: no clone camera"); return; }

                var hd = cam.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                if (hd == null)
                {
                    hd = cam.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                    Plugin.Log.LogInfo("[MinimapController] added HDAdditionalCameraData on clone camera");
                }

                if (tex != null && cam.targetTexture != tex)
                {
                    cam.targetTexture = tex;
                    Plugin.Log.LogInfo($"[MinimapController] wired camera.targetTexture ← miniMapTex ({tex.width}x{tex.height})");
                }
                cam.depth = -100;

                // Copy volume/probe masks from Main_Camera so tonemapping/exposure match
                Camera mainCam = null;
                foreach (var c in Object.FindObjectsOfType<Camera>())
                    if (c.gameObject.name == "Main_Camera") { mainCam = c; break; }
                var mainHd = mainCam?.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                if (mainHd != null)
                {
                    hd.volumeLayerMask = mainHd.volumeLayerMask;
                    hd.probeLayerMask = mainHd.probeLayerMask;
                    hd.volumeAnchorOverride = mainHd.volumeAnchorOverride;
                }

                // Configure per-camera frame settings: disable the shadow/volumetric passes
                // that produce visual noise on the minimap, but LEAVE Postprocess/MotionVectors/
                // SSR/SSAO/ReflectionProbe alone — disabling those starves downstream HDRP
                // passes and produces the "Graphics.CopyTexture 1x1x1 → 1920x1080" error spam.
                ConfigureMinimapFrameSettings(hd);

                // Isolate this camera into its own Volume so day/night in the world doesn't
                // dictate the minimap's brightness.
                if (Plugin.UseFixedExposure.Value)
                    SetupFixedExposureVolume(cam, hd);
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] EnsureHDRPOnClone threw: {e}"); }
        }

        private void ConfigureMinimapFrameSettings(UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData hd)
        {
            try
            {
                hd.customRenderingSettings = true;
                var frameSettings = hd.renderingPathCustomFrameSettings;
                var maskStruct = hd.renderingPathCustomFrameSettingsOverrideMask;

                // ONLY disable things that (a) have no downstream consumers HDRP would
                // then try to blit from a 1x1 dummy and (b) are actual perf/visual wins.
                var toDisable = new[]
                {
                    UnityEngine.Rendering.HighDefinition.FrameSettingsField.ShadowMaps,
                    UnityEngine.Rendering.HighDefinition.FrameSettingsField.ContactShadows,
                    UnityEngine.Rendering.HighDefinition.FrameSettingsField.ScreenSpaceShadows,
                    UnityEngine.Rendering.HighDefinition.FrameSettingsField.Volumetrics,
                    UnityEngine.Rendering.HighDefinition.FrameSettingsField.VolumetricClouds,
                };
                foreach (var f in toDisable)
                {
                    maskStruct.mask[(uint)f] = true;
                    frameSettings.SetEnabled(f, false);
                }

                // Explicitly force Postprocess ON so our fixed-exposure Volume takes effect
                // regardless of what the pipeline default is.
                maskStruct.mask[(uint)UnityEngine.Rendering.HighDefinition.FrameSettingsField.Postprocess] = true;
                frameSettings.SetEnabled(UnityEngine.Rendering.HighDefinition.FrameSettingsField.Postprocess, true);

                hd.renderingPathCustomFrameSettingsOverrideMask = maskStruct;
                hd.renderingPathCustomFrameSettings = frameSettings;
                Plugin.Log.LogInfo($"[MinimapController] frame settings: OFF={{{string.Join(",", System.Array.ConvertAll(toDisable, x => x.ToString()))}}}  ON={{Postprocess}}");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] ConfigureMinimapFrameSettings threw: {e}"); }
        }

        // The layer our exposure volume lives on. Chosen at runtime; cached so the volume
        // and the camera stay in sync across re-enables.
        private int _minimapVolumeLayer = -1;

        private void SetupFixedExposureVolume(Camera cam, UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData hd)
        {
            try
            {
                // Pick a layer that doesn't collide with a named game layer. Prefer high
                // (30..8) — these are usually unused. If everything's named, fall back to 31.
                if (_minimapVolumeLayer < 0)
                {
                    _minimapVolumeLayer = 31;
                    for (int i = 30; i >= 8; i--)
                    {
                        if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { _minimapVolumeLayer = i; break; }
                    }
                }

                // Build a fresh Volume + Profile with a Fixed-EV Exposure override.
                var volGO = new GameObject("MinimapExposureVolume");
                volGO.transform.SetParent(_ourGO.transform, false);
                volGO.layer = _minimapVolumeLayer;

                var vol = volGO.AddComponent<UnityEngine.Rendering.Volume>();
                vol.isGlobal = true;
                vol.priority = 100f;

                var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
                profile.hideFlags = HideFlags.HideAndDontSave;

                var exposure = profile.Add<UnityEngine.Rendering.HighDefinition.Exposure>(overrides: true);
                exposure.mode.overrideState = true;
                exposure.mode.value = UnityEngine.Rendering.HighDefinition.ExposureMode.Fixed;
                exposure.fixedExposure.overrideState = true;
                exposure.fixedExposure.value = Plugin.FixedExposureEV.Value;
                _liveExposure = exposure;

                var colorAdj = profile.Add<UnityEngine.Rendering.HighDefinition.ColorAdjustments>(overrides: true);
                colorAdj.saturation.overrideState = true;
                colorAdj.saturation.value = Plugin.Saturation.Value;
                _liveColorAdj = colorAdj;

                vol.sharedProfile = profile;

                // Camera sees ONLY our layer's volumes — the game's global volume can't leak in
                // and can't drive our exposure via its day/night auto-exposure.
                hd.volumeLayerMask = 1 << _minimapVolumeLayer;

                Plugin.Log.LogInfo($"[MinimapController] fixed-exposure volume ready — layer={_minimapVolumeLayer}, EV={exposure.fixedExposure.value:0.0}");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] SetupFixedExposureVolume threw: {e}"); }
        }

        private void EnsurePlayerIcon()
        {
            try
            {
                // First: make sure the sprite reference is set on our clone.
                // Instantiate copies serialized references, but some Sprite refs may be lost.
                if (_ourCompass.miniMapPlayerIconSprite == null && _gameCompass != null && _gameCompass.miniMapPlayerIconSprite != null)
                {
                    _ourCompass.miniMapPlayerIconSprite = _gameCompass.miniMapPlayerIconSprite;
                    Plugin.Log.LogInfo("[MinimapController] copied miniMapPlayerIconSprite from game compass to clone");
                }

                // Log the clone's UI hierarchy so we can see what's actually there.
                var uiRoot = GetCloneUIRoot();
                if (uiRoot == null) { Plugin.Log.LogWarning("[MinimapController] miniMapUIRoot null on clone"); return; }

                var sb = new System.Text.StringBuilder("[MinimapController] clone MiniMap UI hierarchy:\n");
                DumpChildren(uiRoot, 1, sb);
                Plugin.Log.LogInfo(sb.ToString().TrimEnd());

                // Look for the player-icon element. The game's convention (per UI dump) is
                //   "MiniMap Icon <PlayerName>" with sprite "icon-arrow-white".
                // Match by both: name-contains or sprite name.
                RectTransform playerIconRT = null;
                string matchSource = null;
                foreach (var img in uiRoot.GetComponentsInChildren<UnityEngine.UI.Image>(includeInactive: true))
                {
                    var goName = img.gameObject.name ?? "";
                    var spriteName = img.sprite != null ? img.sprite.name : "";
                    if (goName.IndexOf("PlayerIcon", System.StringComparison.OrdinalIgnoreCase) >= 0
                     || goName.IndexOf("MiniMap Icon ", System.StringComparison.Ordinal) == 0
                     || spriteName == "icon-arrow-white"
                     || spriteName.IndexOf("arrow", System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        playerIconRT = img.rectTransform;
                        matchSource = $"name='{goName}' sprite='{spriteName}'";
                        break;
                    }
                }
                if (playerIconRT != null)
                {
                    // Force-enable the GameObject and its Image/RawImage rendering
                    if (!playerIconRT.gameObject.activeSelf) playerIconRT.gameObject.SetActive(true);
                    foreach (var g in playerIconRT.GetComponents<UnityEngine.UI.Graphic>()) g.enabled = true;
                    _lockedPlayerIconRT = playerIconRT;
                    _lockedPlayerIconScale = playerIconRT.localScale;
                    Plugin.Log.LogInfo($"[MinimapController] player icon found + activated ({matchSource}); locked scale={_lockedPlayerIconScale}");
                }
                else
                {
                    Plugin.Log.LogWarning("[MinimapController] no player-icon element found on clone (checked name-contains 'PlayerIcon' / prefix 'MiniMap Icon ' / sprite 'icon-arrow-white')");
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] EnsurePlayerIcon threw: {e}"); }
        }

        private static void DumpChildren(Transform t, int depth, System.Text.StringBuilder sb)
        {
            var pad = new string(' ', depth * 2);
            var rt = t as RectTransform;
            var img = t.GetComponent<UnityEngine.UI.Graphic>();
            var sprite = img is UnityEngine.UI.Image ii ? (ii.sprite != null ? ii.sprite.name : "(null)") : "-";
            var size = rt != null ? $"size={rt.rect.size}" : "";
            var enabled = img != null ? $" enabled={img.enabled}" : "";
            var active = t.gameObject.activeSelf ? "" : " INACTIVE";
            sb.Append($"{pad}{t.name}   {size}  sprite={sprite}{enabled}{active}\n");
            for (int i = 0; i < t.childCount; i++) DumpChildren(t.GetChild(i), depth + 1, sb);
        }

        private void HideCurtainsOnClone()
        {
            try
            {
                // Instantiate copied the full UI hierarchy including the 4 Black_BG curtain quads
                // and the Image_BG world-map backdrop. On our clone, hide them all — no game impact.
                int hidden = 0;
                foreach (var img in _ourGO.GetComponentsInChildren<UnityEngine.UI.Image>(includeInactive: true))
                {
                    var name = img.gameObject.name;
                    if (name == "Black_BG"
                     || name == "Image_BG"
                     || (img.sprite != null && img.sprite.name == "World_Map_Black"))
                    {
                        if (img.enabled) { img.enabled = false; hidden++; }
                    }
                }
                Plugin.Log.LogInfo($"[MinimapController] hid {hidden} curtain/backdrop image(s) on clone");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] HideCurtainsOnClone threw: {e}"); }
        }

        private void NeuterAutoAddedCameraComponents()
        {
            try
            {
                var cam = GetCloneCamera();
                if (cam == null) return;
                // Same auto-added-component kill list as before, applied to the clone camera
                foreach (var c in cam.gameObject.GetComponents<Component>())
                {
                    if (c == null) continue;
                    var typeName = c.GetType().FullName ?? "";
                    if (typeName.IndexOf("NatureRenderer", System.StringComparison.Ordinal) >= 0
                     || typeName.IndexOf("VisualDesignCafe", System.StringComparison.Ordinal) >= 0
                     || typeName.IndexOf("GPUInstancer", System.StringComparison.Ordinal) >= 0
                     || typeName.IndexOf("Enviro", System.StringComparison.Ordinal) >= 0)
                    {
                        if (c is Behaviour b && b.enabled) { b.enabled = false; Plugin.Log.LogInfo($"[MinimapController] disabled auto-added on clone: {typeName}"); }
                    }
                }
                // Also disable any Renderer / Light on the camera GO
                foreach (var r in cam.gameObject.GetComponents<Renderer>()) { r.enabled = false; }
                foreach (var l in cam.gameObject.GetComponents<Light>()) { l.enabled = false; }
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] NeuterAutoAddedCameraComponents threw: {e}"); }
        }

        private static System.Reflection.MethodInfo _setupMiniMapMI;
        private void InvokeSetupMiniMap()
        {
            try
            {
                if (_setupMiniMapMI == null)
                    _setupMiniMapMI = HarmonyLib.AccessTools.Method(typeof(CompassPro), "SetupMiniMap", new[] { typeof(bool) });
                _setupMiniMapMI?.Invoke(_ourCompass, new object[] { true });
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] SetupMiniMap threw: {e}"); }
        }

        // ---------- Diagnostics ----------

        private static T GetField<T>(object obj, string name) where T : class
        {
            try { var f = HarmonyLib.AccessTools.Field(obj.GetType(), name); return f?.GetValue(obj) as T; }
            catch { return null; }
        }

        private void LogCloneState()
        {
            try
            {
                var cam = GetCloneCamera();
                var tex = GetField<RenderTexture>(_ourCompass, "miniMapTex");
                var img = GetField<UnityEngine.UI.Image>(_ourCompass, "miniMapImage");
                var mainCam = Camera.main;
                var follow = _ourCompass.miniMapFollow;

                Plugin.Log.LogInfo(
                    "[MinimapController] private-clone state:\n"
                    + $"    clone GO: {_ourGO.name}   active={_ourGO.activeInHierarchy}\n"
                    + $"    camera:  name={(cam != null ? cam.gameObject.name : "(null)")}  enabled={(cam != null && cam.enabled)}  targetTex={(cam != null && cam.targetTexture != null ? $"{cam.targetTexture.width}x{cam.targetTexture.height}" : "(null → SCREEN)")}  depth={(cam != null ? cam.depth : 0)}  ortho={(cam != null && cam.orthographic)}\n"
                    + $"    tex:     {(tex != null ? $"{tex.width}x{tex.height}" : "(null)")}\n"
                    + $"    image:   name={(img != null ? img.gameObject.name : "(null)")}   material={(img != null && img.material != null ? img.material.name : "(null)")}\n"
                    + $"    follow:  {(follow != null ? $"{follow.name} @ {follow.position}" : "(null)")}\n"
                    + $"    main cam pos: {(mainCam != null ? mainCam.transform.position.ToString() : "(null)")}");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] LogCloneState threw: {e}"); }
        }

        internal CompassPro Compass => _ourCompass;
    }
}
