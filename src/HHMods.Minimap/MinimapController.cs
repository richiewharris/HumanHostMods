using CompassNavigatorPro;
using HHMods.Core;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace HHMods.Minimap
{
    /// <summary>
    /// Owns the CompassPro reference used for the HUD minimap. Reuses the CompassPro
    /// the game holds in <c>World_Map_Mgr._CompassPro</c>. Bind happens either via a
    /// Harmony postfix on <c>World_Map_Mgr._Start</c> (deterministic) or a slow poll
    /// fallback (includes inactive objects).
    ///
    /// Enable-path uses <c>SetupMiniMap</c>; disable-path uses <c>DisableMiniMap</c>
    /// so CompassPro tears down spawned UI + camera cleanly. Original property values
    /// are saved once and restored after DisableMiniMap.
    /// </summary>
    public class MinimapController : MonoBehaviour
    {
        internal static MinimapController Instance { get; private set; }

        private CompassPro _compass;
        private FilterPanel _filterPanel;
        private SavedState _originalState;
        private bool _minimapOn;

        // Retry state
        private const float ResolveInterval = 2f;
        private float _nextResolveTime;
        private int _resolveAttempts;
        private bool _diagnosticsLoggedOnce;

        private void Awake()
        {
            Instance = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void OnDestroy()
        {
            if (_minimapOn) TryDisableMinimap();
            SceneManager.sceneLoaded -= OnSceneLoaded;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            if (_compass == null)
            {
                _resolveAttempts = 0;
                _nextResolveTime = 0f;
                _diagnosticsLoggedOnce = false;
                Plugin.Log.LogInfo($"[MinimapController] scene loaded: {scene.name} — will retry compass resolution");
            }
        }

        internal void OnWorldMapMgrStarted(global::World_Map_Mgr wm)
        {
            if (_compass != null || wm == null) return;
            if (wm._CompassPro != null) Bind(wm._CompassPro, source: "Harmony(World_Map_Mgr._Start).postfix");
            else Plugin.Log.LogWarning("[MinimapController] World_Map_Mgr._Start ran but _CompassPro was null on completion — will fall back to polling.");
        }

        private void Update()
        {
            if (_compass != null) return;
            if (Time.unscaledTime < _nextResolveTime) return;
            _nextResolveTime = Time.unscaledTime + ResolveInterval;
            TryResolve();
        }

        private void TryResolve()
        {
            _resolveAttempts++;

            var wm = global::World_Map_Mgr.ins;
            if (wm != null && wm._CompassPro != null) { Bind(wm._CompassPro, "World_Map_Mgr.ins._CompassPro"); return; }

            var wm2 = MgrHub.WorldMap;
            if (wm2 != null && wm2._CompassPro != null) { Bind(wm2._CompassPro, "MgrHub.WorldMap._CompassPro"); return; }

            var found = Object.FindObjectsOfType<CompassPro>(includeInactive: true);
            if (found != null && found.Length > 0) { Bind(found[0], $"FindObjectsOfType (found {found.Length})"); return; }

            if (_resolveAttempts == 1 || _resolveAttempts % 15 == 0) LogDiagnostics();
        }

        private void Bind(CompassPro compass, string source)
        {
            _compass = compass;
            Plugin.Log.LogInfo(
                $"[MinimapController] BOUND to CompassPro via {source} (attempt {_resolveAttempts})\n"
                + $"    GameObject: {compass.gameObject.name}   activeInHierarchy={compass.gameObject.activeInHierarchy}\n"
                + $"    (pre-mod) showMiniMap={compass.showMiniMap}   miniMapContents={compass.miniMapContents}   miniMapLocation={compass.miniMapLocation}\n"
                + $"    (pre-mod) miniMapStyle={compass.miniMapStyle}   miniMapPositionAndSize={compass.miniMapPositionAndSize}   keepStraight={compass.miniMapKeepStraight}\n"
                + $"    (pre-mod) miniMapMaskSprite={(compass.miniMapMaskSprite != null ? compass.miniMapMaskSprite.name : "(null)")}   miniMapBorderTexture={(compass.miniMapBorderTexture != null ? compass.miniMapBorderTexture.name : "(null)")}");
            _filterPanel = gameObject.GetComponent<FilterPanel>() ?? gameObject.AddComponent<FilterPanel>();

            if (Plugin.ShowOnStart.Value) TryEnableMinimap();
        }

        // ---------- Enable / disable ----------

        internal void SetVisible(bool visible)
        {
            if (_compass == null) return;
            if (visible) TryEnableMinimap(); else TryDisableMinimap();
        }

        internal bool IsMinimapOn => _minimapOn;

        private void TryEnableMinimap()
        {
            if (_compass == null || _minimapOn) return;

            // Pre-enable snapshot so we can diff what appears
            LogSceneCameras("pre-enable");
            LogMapUIElements("pre-enable");
            LogRawImages("pre-enable");

            SaveOriginalsOnce();

            var follow = ResolveFollowTransform();
            // Keep the game's pre-configured style / mask / border — don't override with SolidCircle.
            // Only override HUD-specific positioning + follow target + activation.
            _compass.miniMapLocation = MINIMAP_LOCATION.TopRight;
            _compass.miniMapPositionAndSize = MINIMAP_POSITION_AND_SIZE.ControlledByCompassNavigatorPro;
            _compass.miniMapSize = Mathf.Clamp01(Plugin.Size.Value / Screen.height);
            _compass.miniMapKeepStraight = Plugin.KeepMapStraight.Value;
            _compass.miniMapAlpha = 1f;
            _compass.miniMapEnableShadows = false;
            // Camera altitude — force it well above the player so the ortho cam looks DOWN
            // at terrain, not up at the sky (which happens when default height leaves it below/inside geo).
            _compass.miniMapCameraMinAltitude = 100f;
            _compass.miniMapCameraMaxAltitude = 1000f;
            _compass.miniMapCameraHeightVSFollow = 250f;   // 250m above the follow transform
            _compass.miniMapCaptureSize = 100f;            // 100m² visible around player
            if (follow != null) _compass.miniMapFollow = follow;
            _compass.showMiniMap = true;

            InvokeSetupMiniMap();

            // Add HDRP camera data to the TopDownCamera so it actually renders geometry
            // under HDRP. Without HDAdditionalCameraData, the ortho camera outputs black.
            EnsureHDRPCameraData();

            // Belt & braces: also hunt for UI Shadow / Outline components anywhere in the
            // MiniMap UI hierarchy and neuter them. CompassPro's TornPaper style ships with
            // heavy border effects that can render weirdly under HDRP.
            NeuterUIEffectsUnderMinimap();

            // CompassPro spawns four "Black_BG" curtain quads to mask everything outside
            // the minimap shape when in world-map mode. In HUD mode they leak onto the
            // main game view (the big black rectangle we've been chasing). Also disable the
            // "Image_BG" world-map backdrop that shows behind the minimap.
            HideMinimapCurtains();

            // Stop CompassPro from stealing the mouse scroll wheel (game uses it for hotbar).
            DisableMinimapScrollZoom();

            _minimapOn = true;
            Plugin.Log.LogInfo(
                $"[MinimapController] HUD minimap ENABLED   follow={(follow != null ? follow.name : "(null)")}   size={_compass.miniMapSize:0.00}   captureSize={_compass.miniMapCaptureSize}   keepStraight={_compass.miniMapKeepStraight}");

            LogPostEnableState();
            LogSceneCameras("post-enable");
            LogMapUIElements("post-enable");
            LogRawImages("post-enable");
            LogCameraTransform();
        }

        private void TryDisableMinimap()
        {
            if (_compass == null || !_minimapOn) return;

            // 1) Re-enable curtain/backdrop images so the game's world-map view works when opened
            RestoreMinimapCurtains();

            // 2) Ask CompassPro to properly tear down the minimap (UI, camera, render texture)
            InvokeDisableMiniMap();

            // 3) Restore the pre-mod property values so any lingering internal state is
            //    consistent with what the game expects.
            RestoreOriginals();

            _minimapOn = false;
            Plugin.Log.LogInfo("[MinimapController] HUD minimap DISABLED (curtains restored, DisableMiniMap called, originals restored)");
        }

        private void LogSceneCameras(string phase)
        {
            try
            {
                var cams = Object.FindObjectsOfType<Camera>(includeInactive: false);
                var sb = new System.Text.StringBuilder($"[MinimapController] {phase} — {cams.Length} active cameras:\n");
                foreach (var c in cams)
                {
                    sb.Append($"    depth={c.depth,4:0}  rect={c.rect}  clearFlags={c.clearFlags} bgColor={c.backgroundColor}  targetTex={(c.targetTexture != null ? c.targetTexture.name + $"({c.targetTexture.width}x{c.targetTexture.height})" : "(null → SCREEN)")}  enabled={c.enabled}  name={c.gameObject.name}\n");
                }
                Plugin.Log.LogInfo(sb.ToString().TrimEnd());
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] LogSceneCameras threw: {e}"); }
        }

        private void LogMapUIElements(string phase)
        {
            try
            {
                // Log the 15 BIGGEST active UI Images on screen — the mystery black
                // rectangle should be one of the largest.
                var all = Object.FindObjectsOfType<UnityEngine.UI.Image>(includeInactive: false);
                var withRects = new System.Collections.Generic.List<(UnityEngine.UI.Image img, float area, Vector3 min, Vector3 max)>();
                var corners = new Vector3[4];
                foreach (var img in all)
                {
                    img.rectTransform.GetWorldCorners(corners);
                    var min = corners[0]; var max = corners[2];
                    var area = (max.x - min.x) * (max.y - min.y);
                    withRects.Add((img, area, min, max));
                }
                withRects.Sort((a, b) => b.area.CompareTo(a.area));
                var sb = new System.Text.StringBuilder($"[MinimapController] {phase} — top 15 UI Images by screen area (of {all.Length} total):\n");
                for (int i = 0; i < System.Math.Min(15, withRects.Count); i++)
                {
                    var e = withRects[i];
                    var name = e.img.gameObject.name;
                    var canvas = e.img.canvas != null ? e.img.canvas.gameObject.name : "(no canvas)";
                    sb.Append($"    #{i+1,2} {name,-32} area={e.area,10:0}   screen=[({e.min.x:0},{e.min.y:0}) → ({e.max.x:0},{e.max.y:0})]   color={e.img.color}   sprite={(e.img.sprite != null ? e.img.sprite.name : "(null)")}   canvas={canvas}\n");
                }
                Plugin.Log.LogInfo(sb.ToString().TrimEnd());
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] LogMapUIElements threw: {e}"); }
        }

        private void LogCameraTransform()
        {
            try
            {
                var cam = GetField<Camera>(_compass, "miniMapCamera");
                if (cam == null) return;
                var t = cam.transform;
                var follow = _compass.miniMapFollow;
                Plugin.Log.LogInfo(
                    $"[MinimapController] TopDownCamera transform:  pos={t.position}   rot(euler)={t.rotation.eulerAngles}   forward={t.forward}   ortho={cam.orthographic}   orthoSize={cam.orthographicSize}   cull=0x{cam.cullingMask:X8}\n"
                    + $"    follow={(follow != null ? $"{follow.name} at {follow.position}" : "(null)")}\n"
                    + $"    heightVSFollow={_compass.miniMapCameraHeightVSFollow}   minAlt={_compass.miniMapCameraMinAltitude}   maxAlt={_compass.miniMapCameraMaxAltitude}   captureSize={_compass.miniMapCaptureSize}");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] LogCameraTransform threw: {e}"); }
        }

        private void NeuterUIEffectsUnderMinimap()
        {
            try
            {
                var uiRoot = GetField<Transform>(_compass, "miniMapUIRoot");
                if (uiRoot == null) { Plugin.Log.LogWarning("[MinimapController] miniMapUIRoot null — can't neuter effects"); return; }
                var effects = uiRoot.GetComponentsInChildren<UnityEngine.UI.BaseMeshEffect>(includeInactive: true);
                int off = 0;
                foreach (var eff in effects)
                {
                    if (!eff.enabled) continue;
                    Plugin.Log.LogInfo($"[MinimapController] disabling UI effect: {eff.GetType().Name} on {eff.gameObject.name}");
                    eff.enabled = false;
                    off++;
                }
                Plugin.Log.LogInfo($"[MinimapController] neutered {off} UI effect(s) under {uiRoot.name}");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] NeuterUIEffectsUnderMinimap threw: {e}"); }
        }

        private void EnsureHDRPCameraData()
        {
            try
            {
                var cam = GetField<Camera>(_compass, "miniMapCamera");
                var tex = GetField<RenderTexture>(_compass, "miniMapTex");
                if (cam == null) { Plugin.Log.LogWarning("[MinimapController] miniMapCamera null — can't add HDRP data"); return; }

                var existing = cam.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                if (existing == null)
                {
                    // Add fresh HDRP camera data with HDRP defaults — don't clone from Main_Camera,
                    // that pulled in settings that made it render to screen instead of the texture.
                    cam.gameObject.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
                    Plugin.Log.LogInfo("[MinimapController] added HDAdditionalCameraData to TopDownCamera (defaults)");
                }
                else
                {
                    Plugin.Log.LogInfo("[MinimapController] HDAdditionalCameraData already present on TopDownCamera");
                }

                // Adding HDRP data can nuke the target texture (HDRP resets camera state on init).
                // Re-force targetTexture so the camera renders to the minimap RenderTexture instead of the screen.
                if (tex != null && cam.targetTexture != tex)
                {
                    cam.targetTexture = tex;
                    Plugin.Log.LogInfo($"[MinimapController] re-forced targetTexture ← miniMapTex ({tex.width}x{tex.height}) after HDRP init");
                }
                // Explicitly cap depth low so if targetTexture ever nulls again, at least it renders under Main_Camera
                cam.depth = -100;
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] EnsureHDRPCameraData threw: {e}"); }
        }

        private void DisableMinimapScrollZoom()
        {
            try
            {
                // CompassPro's MiniMapInteraction handles mouse events including scroll-to-zoom.
                // The game uses scroll for hotbar / weapon switching, so we don't want that hijacked.
                var interactions = Object.FindObjectsOfType<CompassNavigatorPro.MiniMapInteraction>(includeInactive: true);
                foreach (var mi in interactions)
                {
                    if (mi.enabled) { mi.enabled = false; Plugin.Log.LogInfo($"[MinimapController] disabled MiniMapInteraction on {mi.gameObject.name}"); }
                }
                // Belt & braces: clamp zoom bounds so any leftover zoom code can't change scale.
                _compass.miniMapZoomMin = _compass.miniMapZoomLevel;
                _compass.miniMapZoomMax = _compass.miniMapZoomLevel;
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] DisableMinimapScrollZoom threw: {e}"); }
        }

        private void HideMinimapCurtains()
        {
            try
            {
                var uiRoot = GetField<Transform>(_compass, "miniMapUIRoot");
                if (uiRoot == null) { Plugin.Log.LogWarning("[MinimapController] miniMapUIRoot null — can't hide curtains"); return; }
                var images = uiRoot.GetComponentsInChildren<UnityEngine.UI.Image>(includeInactive: true);
                int hidden = 0;
                foreach (var img in images)
                {
                    var name = img.gameObject.name;
                    // Curtain quads that project a solid black backdrop behind the minimap area
                    var isCurtain = name == "Black_BG";
                    // World-map backdrop that leaks into HUD mode
                    var isWorldMapBG = name == "Image_BG"
                                    || (img.sprite != null && img.sprite.name == "World_Map_Black");
                    if (!isCurtain && !isWorldMapBG) continue;
                    if (!img.enabled) continue;
                    img.enabled = false;
                    Plugin.Log.LogInfo($"[MinimapController] hiding {(isCurtain ? "curtain" : "world-map BG")}: {name} (sprite={(img.sprite != null ? img.sprite.name : "(null)")})");
                    hidden++;
                }
                Plugin.Log.LogInfo($"[MinimapController] hid {hidden} minimap curtain/backdrop image(s)");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] HideMinimapCurtains threw: {e}"); }
        }

        private void RestoreMinimapCurtains()
        {
            try
            {
                var uiRoot = GetField<Transform>(_compass, "miniMapUIRoot");
                if (uiRoot == null) return;
                var images = uiRoot.GetComponentsInChildren<UnityEngine.UI.Image>(includeInactive: true);
                foreach (var img in images)
                {
                    var name = img.gameObject.name;
                    if (name == "Black_BG" || name == "Image_BG"
                     || (img.sprite != null && img.sprite.name == "World_Map_Black"))
                    {
                        img.enabled = true;
                    }
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] RestoreMinimapCurtains threw: {e}"); }
        }

        private void LogRawImages(string phase)
        {
            try
            {
                // RawImage is what CompassPro uses to display the render texture — also worth checking
                var all = Object.FindObjectsOfType<UnityEngine.UI.RawImage>(includeInactive: false);
                if (all.Length == 0) { Plugin.Log.LogInfo($"[MinimapController] {phase} — no active RawImages"); return; }
                var sb = new System.Text.StringBuilder($"[MinimapController] {phase} — {all.Length} active RawImages:\n");
                var corners = new Vector3[4];
                foreach (var img in all)
                {
                    img.rectTransform.GetWorldCorners(corners);
                    var min = corners[0]; var max = corners[2];
                    var area = (max.x - min.x) * (max.y - min.y);
                    var tex = img.texture;
                    sb.Append($"    {img.gameObject.name,-32} area={area,10:0}   screen=[({min.x:0},{min.y:0}) → ({max.x:0},{max.y:0})]   texture={(tex != null ? $"{tex.name} {tex.width}x{tex.height}" : "(null)")}   color={img.color}\n");
                }
                Plugin.Log.LogInfo(sb.ToString().TrimEnd());
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] LogRawImages threw: {e}"); }
        }

        private static T GetField<T>(object obj, string name) where T : class
        {
            try
            {
                var f = HarmonyLib.AccessTools.Field(obj.GetType(), name);
                return f?.GetValue(obj) as T;
            }
            catch { return null; }
        }

        private static string BoolField(object obj, string name)
        {
            try
            {
                var f = HarmonyLib.AccessTools.Field(obj.GetType(), name);
                if (f == null) return "?";
                return f.GetValue(obj)?.ToString() ?? "null";
            }
            catch { return "?"; }
        }

        private void LogPostEnableState()
        {
            if (_compass == null) return;
            try
            {
                var cam    = GetField<Camera>(_compass, "miniMapCamera");
                var tex    = GetField<RenderTexture>(_compass, "miniMapTex");
                var uiRoot = GetField<Transform>(_compass, "miniMapUIRoot");
                var ui     = GetField<Transform>(_compass, "miniMapUI");
                var mask   = GetField<Transform>(_compass, "miniMapMaskUI");
                var img    = GetField<UnityEngine.UI.Image>(_compass, "miniMapImage");
                var fsPh   = _compass.miniMapFullScreenPlaceholder;

                Plugin.Log.LogInfo(
                    "[MinimapController] post-enable state:\n"
                    + $"    miniMapCamera:   {(cam != null ? cam.gameObject.name : "(null)")}   enabled={(cam != null && cam.enabled)}   targetTexture={(cam != null && cam.targetTexture != null ? cam.targetTexture.name : "(null)")}   rect={(cam != null ? cam.rect.ToString() : "-")}\n"
                    + $"    miniMapTex:      {(tex != null ? $"{tex.name} {tex.width}x{tex.height}" : "(null)")}\n"
                    + $"    miniMapImage:    {(img != null ? img.name : "(null)")}   sprite={(img != null && img.sprite != null ? img.sprite.name : "(null)")}   material={(img != null && img.material != null ? img.material.name : "(null)")}\n"
                    + $"    miniMapUIRoot:   {(uiRoot != null ? $"{uiRoot.name} active={uiRoot.gameObject.activeInHierarchy}" : "(null)")}\n"
                    + $"    miniMapUI:       {(ui != null ? $"{ui.name} active={ui.gameObject.activeInHierarchy}" : "(null)")}\n"
                    + $"    miniMapMaskUI:   {(mask != null ? $"{mask.name} active={mask.gameObject.activeInHierarchy}" : "(null)")}\n"
                    + $"    miniMapFullScreenPlaceholder: {(fsPh != null ? $"{fsPh.name} active={fsPh.gameObject.activeInHierarchy}" : "(null)")}\n"
                    + $"    miniMapZoomState={_compass.miniMapZoomState}");
            }
            catch (System.Exception e)
            {
                Plugin.Log.LogError($"[MinimapController] LogPostEnableState threw: {e}");
            }
        }

        private Transform ResolveFollowTransform()
        {
            // Prefer the actual player character (has Player_Input; NPCs have NPC_Input).
            var players = Object.FindObjectsOfType<global::Player_Input>(includeInactive: false);
            if (players != null && players.Length > 0) return players[0].transform;

            // Fallback: Camera.main. On this game that returned the GPU Instancer culling camera,
            // which happens to track the player but is confusing in logs.
            var cam = Camera.main;
            if (cam != null) return cam.transform;

            return null;
        }

        // ---------- Reflected CompassPro calls ----------

        private static System.Reflection.MethodInfo _setupMiniMapMI;
        private static System.Reflection.MethodInfo _disableMiniMapMI;

        private void InvokeSetupMiniMap()
        {
            try
            {
                if (_setupMiniMapMI == null)
                    _setupMiniMapMI = HarmonyLib.AccessTools.Method(typeof(CompassPro), "SetupMiniMap", new[] { typeof(bool) });
                if (_setupMiniMapMI != null) _setupMiniMapMI.Invoke(_compass, new object[] { true });
                else Plugin.Log.LogWarning("[MinimapController] SetupMiniMap not found via reflection");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] SetupMiniMap threw: {e}"); }

            // Workaround for a CompassPro order-of-ops issue: it creates a RenderTexture
            // (miniMapTex) but doesn't reliably assign it to the camera's targetTexture.
            // Without this fix, the ortho TopDownCamera renders to SCREEN with a fullscreen
            // viewport (0,0,1,1), producing a big black rectangle covering the game view.
            try
            {
                var cam = GetField<Camera>(_compass, "miniMapCamera");
                var tex = GetField<RenderTexture>(_compass, "miniMapTex");
                if (cam != null && tex != null && cam.targetTexture != tex)
                {
                    cam.targetTexture = tex;
                    Plugin.Log.LogInfo($"[MinimapController] fixup: miniMapCamera.targetTexture ← miniMapTex ({tex.width}x{tex.height})");
                }
                else if (cam == null || tex == null)
                {
                    Plugin.Log.LogWarning($"[MinimapController] cannot wire targetTexture — cam={(cam != null)} tex={(tex != null)}");
                }
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] targetTexture fixup threw: {e}"); }
        }

        private void InvokeDisableMiniMap()
        {
            try
            {
                if (_disableMiniMapMI == null)
                    _disableMiniMapMI = HarmonyLib.AccessTools.Method(typeof(CompassPro), "DisableMiniMap");
                if (_disableMiniMapMI != null) _disableMiniMapMI.Invoke(_compass, null);
                else Plugin.Log.LogWarning("[MinimapController] DisableMiniMap not found via reflection");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapController] DisableMiniMap threw: {e}"); }
        }

        // ---------- Save / restore ----------

        private class SavedState
        {
            public bool showMiniMap;
            public MINIMAP_CONTENTS contents;
            public MINIMAP_LOCATION location;
            public MINIMAP_POSITION_AND_SIZE positionAndSize;
            public MINIMAP_STYLE style;
            public MINIMAP_CAMERA_MODE cameraMode;
            public MINIMAP_CAMERA_SNAPSHOT_FREQUENCY snapshotFreq;
            public float size, captureSize, alpha, cameraMinAlt, cameraMaxAlt, cameraHeightVSFollow;
            public int layerMask;
            public bool keepStraight, keepAspect, enableShadows;
            public Transform follow;
        }

        private void SaveOriginalsOnce()
        {
            if (_originalState != null) return;
            _originalState = new SavedState
            {
                showMiniMap = _compass.showMiniMap,
                contents = _compass.miniMapContents,
                location = _compass.miniMapLocation,
                positionAndSize = _compass.miniMapPositionAndSize,
                style = _compass.miniMapStyle,
                cameraMode = _compass.miniMapCameraMode,
                snapshotFreq = _compass.miniMapCameraSnapshotFrequency,
                size = _compass.miniMapSize,
                captureSize = _compass.miniMapCaptureSize,
                alpha = _compass.miniMapAlpha,
                cameraMinAlt = _compass.miniMapCameraMinAltitude,
                cameraMaxAlt = _compass.miniMapCameraMaxAltitude,
                cameraHeightVSFollow = _compass.miniMapCameraHeightVSFollow,
                layerMask = _compass.miniMapLayerMask,
                keepStraight = _compass.miniMapKeepStraight,
                keepAspect = _compass.miniMapKeepAspectRatio,
                enableShadows = _compass.miniMapEnableShadows,
                follow = _compass.miniMapFollow,
            };
        }

        private void RestoreOriginals()
        {
            if (_originalState == null || _compass == null) return;
            var s = _originalState;
            _compass.showMiniMap = s.showMiniMap;
            _compass.miniMapContents = s.contents;
            _compass.miniMapLocation = s.location;
            _compass.miniMapPositionAndSize = s.positionAndSize;
            _compass.miniMapStyle = s.style;
            _compass.miniMapCameraMode = s.cameraMode;
            _compass.miniMapCameraSnapshotFrequency = s.snapshotFreq;
            _compass.miniMapSize = s.size;
            _compass.miniMapCaptureSize = s.captureSize;
            _compass.miniMapAlpha = s.alpha;
            _compass.miniMapCameraMinAltitude = s.cameraMinAlt;
            _compass.miniMapCameraMaxAltitude = s.cameraMaxAlt;
            _compass.miniMapCameraHeightVSFollow = s.cameraHeightVSFollow;
            _compass.miniMapLayerMask = s.layerMask;
            _compass.miniMapKeepStraight = s.keepStraight;
            _compass.miniMapKeepAspectRatio = s.keepAspect;
            _compass.miniMapEnableShadows = s.enableShadows;
            _compass.miniMapFollow = s.follow;
        }

        // ---------- Diagnostics ----------

        private void LogDiagnostics()
        {
            var hub = MgrHub.HubOrNull;
            var wm_ins = global::World_Map_Mgr.ins;
            var wm_hub = MgrHub.WorldMap;
            var scene = SceneManager.GetActiveScene();
            var loadedScenes = new System.Text.StringBuilder();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var s = SceneManager.GetSceneAt(i);
                if (loadedScenes.Length > 0) loadedScenes.Append(", ");
                loadedScenes.Append($"{s.name}({(s.isLoaded ? "loaded" : "unloaded")})");
            }
            var msg = $"[MinimapController] resolve attempt #{_resolveAttempts} — no CompassPro yet.\n"
                    + $"    active scene: {scene.name}   all: {loadedScenes}\n"
                    + $"    Mgr_Hub: {(hub != null ? "found" : "NULL")}\n"
                    + $"    World_Map_Mgr.ins: {(wm_ins != null ? "found" : "NULL")}\n"
                    + $"    Mgr_Hub._WorldMapMgr: {(wm_hub != null ? "found" : "NULL")}\n"
                    + $"    ._CompassPro via ins:  {(wm_ins != null && wm_ins._CompassPro != null ? "set" : "unset")}\n"
                    + $"    ._CompassPro via hub:   {(wm_hub != null && wm_hub._CompassPro != null ? "set" : "unset")}";
            if (!_diagnosticsLoggedOnce) { Plugin.Log.LogWarning(msg); _diagnosticsLoggedOnce = true; }
            else Plugin.Log.LogInfo(msg);
        }

        internal CompassPro Compass => _compass;
    }
}
