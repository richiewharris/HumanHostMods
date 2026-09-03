using HarmonyLib;
using HHMods.Core;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace HHMods.Minimap.V2
{
    /// <summary>
    /// From-scratch minimap. Owns its own Camera + RenderTexture + Canvas, no CompassPro
    /// involvement. Design goal: no framework-fighting — nothing else is scaling icons,
    /// resetting exposure, or repositioning our render target between frames.
    ///
    /// This is the walking-skeleton pass. Enough to render a top-down view of the world
    /// into a HUD panel. Player arrow, north marker, POI overlay, and scoped light are
    /// follow-up iterations.
    /// </summary>
    public class MinimapV2Controller : MonoBehaviour
    {
        internal static MinimapV2Controller Instance { get; private set; }

        // --- Render side ---
        private GameObject _camGO;
        private Camera _cam;
        private RenderTexture _rt;
        private UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData _hd;

        // Scoped directional light (only enabled during our render pass) + isolated volume
        // giving us fixed exposure + saturation independent of the game's day/night cycle.
        private GameObject _lightGO;
        private Light _light;
        private UnityEngine.Rendering.HighDefinition.HDAdditionalLightData _lightHD;
        private GameObject _volumeGO;
        private UnityEngine.Rendering.Volume _volume;
        private UnityEngine.Rendering.HighDefinition.Exposure _liveExposure;
        private UnityEngine.Rendering.HighDefinition.ColorAdjustments _liveColorAdj;
        private bool _renderCallbacksRegistered;

        // --- UI side ---
        private GameObject _canvasGO;
        private Canvas _canvas;
        private RawImage _mapImage;
        private RectTransform _rootRT;
        private RectTransform _maskRT;
        private RectTransform _playerArrowRT;
        private RectTransform _poiRootRT;   // parent of all POI marker RTs; child of _maskRT so markers clip to the circle
        private Camera _cachedMainCam;

        // --- POI overlay state ---
        private readonly System.Collections.Generic.List<PoiMarker> _pois = new System.Collections.Generic.List<PoiMarker>();
        private float _nextPoiScan;
        private Sprite _poiFallbackSprite;

        private class PoiMarker
        {
            public global::CompassNavigatorPro.CompassProPOI Src;
            public RectTransform Rt;
            public Image Img;
        }

        // Cached sprites — resolved from game assets or procedurally generated once at build time.
        private Sprite _circleMaskSprite;
        private Sprite _arrowSprite;
        private Sprite _kiteArrowSprite;
        private Sprite _compassRoseSprite;
        private Sprite _cardinalDiamondSprite;
        private Sprite _ordinalDiamondSprite;
        private Sprite _scrollworkSprite;
        private Sprite _ringSprite;

        // --- Runtime state ---
        private bool _visible;
        private Transform _followTarget;
        private float _nextFollowResolveTime;
        private float _nextDiagLog;

        private void Awake()
        {
            Instance = this;
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        private void Start()
        {
            BuildRender();
            BuildLightAndVolume();
            BuildUI();
            RegisterRenderCallbacks();
            SetVisible(Plugin.ShowOnStart.Value);
            Plugin.Log.LogInfo($"[MinimapV2] initialized (cam={(_cam!=null?"ok":"null")}, rt={(_rt!=null?_rt.width+"x"+_rt.height:"null")}, canvas={(_canvas!=null?"ok":"null")}, visible={_visible})");
        }

        private void OnDestroy()
        {
            SceneManager.sceneLoaded -= OnSceneLoaded;
            UnregisterRenderCallbacks();
            if (_camGO != null) Destroy(_camGO);
            if (_lightGO != null) Destroy(_lightGO);
            if (_volumeGO != null) Destroy(_volumeGO);
            if (_canvasGO != null) Destroy(_canvasGO);
            if (_rt != null) Destroy(_rt);
            if (Instance == this) Instance = null;
        }

        private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
        {
            // Player transform is scene-scoped — will be re-resolved on next LateUpdate.
            _followTarget = null;
            Plugin.Log.LogInfo($"[MinimapV2] scene loaded ({scene.name}) — follow target cleared, will re-resolve");
        }

        // ---------- Render stack ----------

        private void BuildRender()
        {
            _rt = new RenderTexture(1024, 1024, 16, RenderTextureFormat.ARGB32);
            _rt.name = "HHMods.MinimapV2.RT";
            _rt.hideFlags = HideFlags.HideAndDontSave;
            _rt.Create();

            _camGO = new GameObject("HHMods.MinimapV2.Camera");
            DontDestroyOnLoad(_camGO);
            _camGO.hideFlags = HideFlags.HideAndDontSave;

            _cam = _camGO.AddComponent<Camera>();
            _cam.orthographic = true;
            _cam.orthographicSize = Plugin.ZoomCaptureSize.Value * 0.5f;
            _cam.nearClipPlane = 1f;
            _cam.farClipPlane = 3000f;
            _cam.clearFlags = CameraClearFlags.SolidColor;
            _cam.backgroundColor = new Color(0.03f, 0.03f, 0.03f, 1f);
            _cam.targetTexture = _rt;
            _cam.depth = -100;                   // renders before main camera
            _cam.allowHDR = true;
            _cam.cullingMask = ComputeCullingMask();

            _hd = _camGO.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();

            // Copy volume / probe layer masks from Main_Camera as a fallback baseline; we
            // override volumeLayerMask below to isolate our exposure to a private layer.
            var mainCam = ResolveMainCamera();
            var mainHD = mainCam?.GetComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData>();
            if (mainHD != null)
            {
                _hd.volumeLayerMask = mainHD.volumeLayerMask;
                _hd.probeLayerMask = mainHD.probeLayerMask;
                _hd.volumeAnchorOverride = mainHD.volumeAnchorOverride;
            }

            // HDRP frame settings — kill shadow / volumetric / cloud passes on the minimap
            // camera (they're heavy AND cast unwanted shadows onto the top-down render), keep
            // Postprocess enabled so our fixed-exposure volume takes effect.
            ConfigureFrameSettings();
        }

        private void ConfigureFrameSettings()
        {
            try
            {
                _hd.customRenderingSettings = true;
                var frame = _hd.renderingPathCustomFrameSettings;
                var mask = _hd.renderingPathCustomFrameSettingsOverrideMask;

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
                    mask.mask[(uint)f] = true;
                    frame.SetEnabled(f, false);
                }
                mask.mask[(uint)UnityEngine.Rendering.HighDefinition.FrameSettingsField.Postprocess] = true;
                frame.SetEnabled(UnityEngine.Rendering.HighDefinition.FrameSettingsField.Postprocess, true);

                _hd.renderingPathCustomFrameSettingsOverrideMask = mask;
                _hd.renderingPathCustomFrameSettings = frame;
                Plugin.Log.LogInfo("[MinimapV2] frame settings applied (shadows/volumetrics OFF, postprocess ON)");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapV2] ConfigureFrameSettings threw: {e}"); }
        }

        // ---------- Scoped light + isolated exposure volume ----------

        private void BuildLightAndVolume()
        {
            try
            {
                // Isolated layer for our volume so the game's global volume can't touch our exposure.
                int layer = 31;
                for (int i = 30; i >= 8; i--)
                    if (string.IsNullOrEmpty(LayerMask.LayerToName(i))) { layer = i; break; }

                // Volume with Fixed Exposure + Saturation
                _volumeGO = new GameObject("HHMods.MinimapV2.Volume");
                DontDestroyOnLoad(_volumeGO);
                _volumeGO.hideFlags = HideFlags.HideAndDontSave;
                _volumeGO.layer = layer;
                _volume = _volumeGO.AddComponent<UnityEngine.Rendering.Volume>();
                _volume.isGlobal = true;
                _volume.priority = 100f;
                var profile = ScriptableObject.CreateInstance<UnityEngine.Rendering.VolumeProfile>();
                profile.hideFlags = HideFlags.HideAndDontSave;
                _liveExposure = profile.Add<UnityEngine.Rendering.HighDefinition.Exposure>(overrides: true);
                _liveExposure.mode.overrideState = true;
                _liveExposure.mode.value = UnityEngine.Rendering.HighDefinition.ExposureMode.Fixed;
                _liveExposure.fixedExposure.overrideState = true;
                _liveExposure.fixedExposure.value = Plugin.FixedExposureEV.Value;
                _liveColorAdj = profile.Add<UnityEngine.Rendering.HighDefinition.ColorAdjustments>(overrides: true);
                _liveColorAdj.saturation.overrideState = true;
                _liveColorAdj.saturation.value = Plugin.Saturation.Value;
                _volume.sharedProfile = profile;

                _hd.volumeLayerMask = 1 << layer;

                // Directional light, disabled until our render callback fires.
                _lightGO = new GameObject("HHMods.MinimapV2.Light");
                DontDestroyOnLoad(_lightGO);
                _lightGO.hideFlags = HideFlags.HideAndDontSave;
                _lightGO.transform.rotation = Quaternion.Euler(Plugin.LightAngleX.Value, Plugin.LightAngleY.Value, 0f);
                _light = _lightGO.AddComponent<Light>();
                _light.type = LightType.Directional;
                _light.shadows = LightShadows.None;
                _light.color = Color.white;
                _light.intensity = 3f;
                _lightHD = _lightGO.AddComponent<UnityEngine.Rendering.HighDefinition.HDAdditionalLightData>();
                _lightHD.EnableShadows(false);
                ApplyLightIntensityToHD(_lightHD, Plugin.LightIntensityLux.Value);
                _lightGO.SetActive(false);

                Plugin.Log.LogInfo($"[MinimapV2] light+volume built (layer={layer}, EV={_liveExposure.fixedExposure.value}, sat={_liveColorAdj.saturation.value}, lux={Plugin.LightIntensityLux.Value})");
            }
            catch (System.Exception e) { Plugin.Log.LogError($"[MinimapV2] BuildLightAndVolume threw: {e}"); }
        }

        private static void ApplyLightIntensityToHD(UnityEngine.Rendering.HighDefinition.HDAdditionalLightData hd, float lux)
        {
            if (hd == null) return;
            var mi = typeof(UnityEngine.Rendering.HighDefinition.HDAdditionalLightData)
                .GetMethod("SetIntensity", new[] { typeof(float), typeof(UnityEngine.Rendering.HighDefinition.LightUnit) });
            if (mi != null)
                mi.Invoke(hd, new object[] { lux, UnityEngine.Rendering.HighDefinition.LightUnit.Lux });
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

        private void OnBeginCameraRender(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _cam) return;
            if (_lightGO != null && !_lightGO.activeSelf) _lightGO.SetActive(true);
        }

        private void OnEndCameraRender(UnityEngine.Rendering.ScriptableRenderContext ctx, Camera cam)
        {
            if (cam != _cam) return;
            if (_lightGO != null && _lightGO.activeSelf) _lightGO.SetActive(false);
        }

        private static int ComputeCullingMask()
        {
            int mask = ~0;
            if (Plugin.CullTransparentFX.Value) mask &= ~(1 << 1);
            if (Plugin.CullWater.Value)         mask &= ~(1 << 4);
            if (Plugin.CullUI.Value)            mask &= ~(1 << 5);
            return mask;
        }

        // ---------- UI stack ----------

        private void BuildUI()
        {
            _canvasGO = new GameObject("HHMods.MinimapV2.Canvas");
            DontDestroyOnLoad(_canvasGO);
            _canvasGO.hideFlags = HideFlags.HideAndDontSave;

            _canvas = _canvasGO.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            _canvas.sortingOrder = 5000;      // above the game's UI
            _canvasGO.AddComponent<CanvasScaler>();
            var raycaster = _canvasGO.AddComponent<GraphicRaycaster>();
            raycaster.ignoreReversedGraphics = true;

            _circleMaskSprite = ResolveOrGenerateCircleSprite();

            // ---- Layout math -----------------------------------------------------------------
            // Root sizeDelta = mask diameter (all children position relative to root center).
            // The chrome frame extends `frameThickness` past the root on all sides; the compass
            // badge sticks up further still. Padding compensates so nothing bleeds off the screen
            // right edge or under the top HUD bar.
            var size = Plugin.Size.Value;
            var frameThickness = 20f;
            var frameOuterSize = size + frameThickness * 2f;
            var compassSize = Mathf.Max(38f, size * 0.16f);            // responsive to mask size
            var overlapY = compassSize * (1f / 3f);
            var compassOverhangTop = Mathf.Max(0f, compassSize * 0.5f + overlapY - frameThickness);
            var rightPad = 8f + frameThickness;
            // Top clearance = 8px screen margin + 26px coord bar height + 6px gap. Keeps the
            // compass badge tucked directly under the coord bar with a small breathing gap.
            var topPad = (8f + 26f + 6f) + frameThickness + compassOverhangTop;

            // Root — top-right, below the HUD bar with room for the compass badge poking up
            var rootGO = new GameObject("MinimapRoot");
            _rootRT = rootGO.AddComponent<RectTransform>();
            _rootRT.SetParent(_canvas.transform, false);
            _rootRT.anchorMin = new Vector2(1f, 1f);
            _rootRT.anchorMax = new Vector2(1f, 1f);
            _rootRT.pivot = new Vector2(1f, 1f);
            _rootRT.sizeDelta = new Vector2(size, size);
            _rootRT.anchoredPosition = new Vector2(-rightPad, -topPad);

            // --- Chrome frame — thick dark ring behind the mask ---
            var frameGO = new GameObject("FrameChrome");
            var frameRT = frameGO.AddComponent<RectTransform>();
            frameRT.SetParent(_rootRT, false);
            frameRT.anchorMin = new Vector2(0.5f, 0.5f);
            frameRT.anchorMax = new Vector2(0.5f, 0.5f);
            frameRT.pivot = new Vector2(0.5f, 0.5f);
            frameRT.sizeDelta = new Vector2(frameOuterSize, frameOuterSize);
            frameRT.anchoredPosition = Vector2.zero;
            var frameImg = frameGO.AddComponent<Image>();
            frameImg.sprite = _circleMaskSprite;
            frameImg.color = new Color(0.17f, 0.16f, 0.14f, 1f);   // #2b2823 chrome dark
            frameImg.raycastTarget = false;

            // --- Hairlines: inner (r=158.5), outer (r=181.5), and faint middle (r=176) ---
            // Scale factor: SVG chrome ring radius = 170 (~50% of viewBox). My frame outer
            // radius = frameOuterSize/2. Diameters below are in map pixels.
            AddHighlightRing(size + 2f,             new Color(0.44f, 0.40f, 0.32f, 1.0f));   // inner hairline (r=158.5 in spec)
            AddHighlightRing(frameOuterSize - 2f,   new Color(0.44f, 0.40f, 0.32f, 1.0f));   // outer hairline (r=181.5 in spec)
            // Fainter middle hairline (spec r=176 → 176/170 = 1.035x chrome-center radius)
            AddHighlightRing((size + frameThickness) + frameThickness * 0.5f,  new Color(0.29f, 0.27f, 0.22f, 0.8f));

            // --- Scrollwork ring — 12 volute pairs procedurally rasterized from the SVG paths ---
            _scrollworkSprite = MakeScrollworkRingSprite();
            var scrollGO = new GameObject("Scrollwork");
            var scrollRT = scrollGO.AddComponent<RectTransform>();
            scrollRT.SetParent(_rootRT, false);
            scrollRT.anchorMin = new Vector2(0.5f, 0.5f);
            scrollRT.anchorMax = new Vector2(0.5f, 0.5f);
            scrollRT.pivot = new Vector2(0.5f, 0.5f);
            // Scrollwork sits ON the chrome band, sized to match the outer chrome radius so it
            // "rides" on top of the dark band. Slight inset from outer edge for clean framing.
            scrollRT.sizeDelta = new Vector2(frameOuterSize - 4f, frameOuterSize - 4f);
            scrollRT.anchoredPosition = Vector2.zero;
            var scrollImg = scrollGO.AddComponent<Image>();
            scrollImg.sprite = _scrollworkSprite;
            scrollImg.color = new Color(1f, 1f, 1f, 0.9f);
            scrollImg.raycastTarget = false;

            // --- Cardinal + ordinal diamond markers on the frame ---
            _cardinalDiamondSprite = MakeDiamondSprite(widthRatio: 12f / 14f);   // 12w × 14h per spec
            _ordinalDiamondSprite  = MakeDiamondSprite(widthRatio: 1f);          // 7×7 square
            var markerRadius = (size + frameThickness) * 0.5f;
            // Brighter than spec so they read against the dark chrome instead of blending in.
            var cardinalColor = new Color(0.83f, 0.74f, 0.56f, 1f);   // #d4be8f warm cream
            var ordinalColor  = new Color(0.61f, 0.53f, 0.38f, 1f);   // #9c8861 lighter brass
            // Spec is 12×14 on a 340px frame. Bump 1.5x so they read at HUD distance.
            var markerScale = (frameOuterSize / 340f) * 1.5f;
            for (int i = 0; i < 8; i++)
            {
                var angleDeg = 90f - i * 45f;   // 0=N (top), then E, S, W, then diagonals
                var isCardinal = (i % 2) == 0;
                var color = isCardinal ? cardinalColor : ordinalColor;
                var mw = isCardinal ? 12f * markerScale : 7f * markerScale;
                var mh = isCardinal ? 14f * markerScale : 7f * markerScale;
                AddFrameMarker(markerRadius, angleDeg, mw, mh, color, isCardinal);
            }

            // --- Circular mask container ---
            // The mask sprite defines the clipped shape; child graphics render only inside it.
            var maskGO = new GameObject("MaskContainer");
            _maskRT = maskGO.AddComponent<RectTransform>();
            _maskRT.SetParent(_rootRT, false);
            _maskRT.anchorMin = Vector2.zero;
            _maskRT.anchorMax = Vector2.one;
            _maskRT.pivot = new Vector2(0.5f, 0.5f);
            _maskRT.sizeDelta = Vector2.zero;
            var maskImg = maskGO.AddComponent<Image>();
            maskImg.sprite = _circleMaskSprite;
            maskImg.raycastTarget = false;
            var mask = maskGO.AddComponent<Mask>();
            mask.showMaskGraphic = false;   // don't render the mask sprite itself, just use its alpha

            // --- Map RawImage — child of mask, gets clipped ---
            var mapGO = new GameObject("MapImage");
            var mapRT = mapGO.AddComponent<RectTransform>();
            mapRT.SetParent(_maskRT, false);
            mapRT.anchorMin = Vector2.zero;
            mapRT.anchorMax = Vector2.one;
            mapRT.pivot = new Vector2(0.5f, 0.5f);
            mapRT.sizeDelta = Vector2.zero;
            mapRT.anchoredPosition = Vector2.zero;
            _mapImage = mapGO.AddComponent<RawImage>();
            _mapImage.texture = _rt;
            _mapImage.raycastTarget = false;

            // --- Player arrow — procedural kite shape with baked-in white outline + dark fill.
            // Matches the mockup: pointed apex up, wing tips out, notched bottom center.
            _kiteArrowSprite = MakeKitePlayerArrowSprite();
            _arrowSprite = ResolveArrowSprite();   // fallback for compass rose lookups
            var arrowGO = new GameObject("PlayerArrow");
            _playerArrowRT = arrowGO.AddComponent<RectTransform>();
            _playerArrowRT.SetParent(_maskRT, false);
            _playerArrowRT.anchorMin = new Vector2(0.5f, 0.5f);
            _playerArrowRT.anchorMax = new Vector2(0.5f, 0.5f);
            _playerArrowRT.pivot = new Vector2(0.5f, 0.5f);
            // Sprite is 52×72 (SVG 26×36 × 2); preserve that aspect at the configured size.
            var arrowH = Mathf.Max(Plugin.ArrowSize.Value * 0.8f, 26f);
            _playerArrowRT.sizeDelta = new Vector2(arrowH * (52f / 72f), arrowH);
            _playerArrowRT.anchoredPosition = Vector2.zero;
            var arrowImg = arrowGO.AddComponent<Image>();
            arrowImg.raycastTarget = false;
            arrowImg.sprite = _kiteArrowSprite;
            arrowImg.color = Color.white;   // outline is white, fill is baked into sprite alpha

            // --- POI overlay container — child of the mask so markers clip cleanly at the circle.
            //     Rendered UNDER the player arrow but OVER the map RawImage, so POIs sit on top
            //     of the world but the player marker remains dominant. Anchored to fill the mask
            //     so anchoredPosition on child markers is measured from map center in pixels.
            var poiRootGO = new GameObject("POIRoot");
            _poiRootRT = poiRootGO.AddComponent<RectTransform>();
            _poiRootRT.SetParent(_maskRT, false);
            _poiRootRT.anchorMin = Vector2.zero;
            _poiRootRT.anchorMax = Vector2.one;
            _poiRootRT.sizeDelta = Vector2.zero;
            _poiRootRT.SetSiblingIndex(_playerArrowRT != null ? _playerArrowRT.GetSiblingIndex() : 1);
            _poiFallbackSprite = MakePoiDotSprite();

            // --- North compass badge — dark disc + cream heavy rim + white compass rose star ---
            // Overlaps the top of the mask by ~1/3, straddling the chrome frame.
            // (compassSize + overlapY hoisted to layout math block above so root padding fits.)
            _compassRoseSprite = MakeCompassRoseSprite();
            _ringSprite = MakeRingSprite();

            var compassGO = new GameObject("NorthCompass");
            var compassRT = compassGO.AddComponent<RectTransform>();
            compassRT.SetParent(_rootRT, false);
            compassRT.anchorMin = new Vector2(0.5f, 1f);
            compassRT.anchorMax = new Vector2(0.5f, 1f);
            compassRT.pivot = new Vector2(0.5f, 0.5f);
            compassRT.anchoredPosition = new Vector2(0f, overlapY);
            compassRT.sizeDelta = new Vector2(compassSize, compassSize);

            // Dark semi-transparent disc background
            var discImg = compassGO.AddComponent<Image>();
            discImg.sprite = _circleMaskSprite;
            discImg.color = new Color(0f, 0f, 0f, 0.6f);
            discImg.raycastTarget = false;

            // Cream heavy rim — a ring sprite as a child, slightly transparent so it looks like a bevel
            var rimGO = new GameObject("Rim");
            var rimRT = rimGO.AddComponent<RectTransform>();
            rimRT.SetParent(compassRT, false);
            rimRT.anchorMin = Vector2.zero;
            rimRT.anchorMax = Vector2.one;
            rimRT.sizeDelta = Vector2.zero;
            var rimImg = rimGO.AddComponent<Image>();
            rimImg.sprite = _ringSprite;
            rimImg.color = new Color(0.85f, 0.82f, 0.76f, 0.55f);   // #d9d2c1 at ~55% alpha
            rimImg.raycastTarget = false;

            // Four intercardinal rays (NE / NW / SE / SW) — thin cream lines from mid-radius
            // to outer rim, at 45° / 135° / 225° / 315°. Faint (0.45 alpha) so the star reads first.
            AddCompassRay(compassRT, compassSize,  45f);
            AddCompassRay(compassRT, compassSize, 135f);
            AddCompassRay(compassRT, compassSize, 225f);
            AddCompassRay(compassRT, compassSize, 315f);

            // --- Zoom buttons on the frame — two circular badges stacked on the RIGHT side,
            //     mirroring the compass rose placement pattern (straddle the bezel with ~1/6
            //     of the button overlapping into the mask, rest sitting outside on the chrome).
            //     Plus above equator, minus below. Same side per SVG mock convention.
            var buttonSize = Mathf.Max(32f, size * 0.14f);
            AddZoomButton(offsetAngleDeg: +30f, buttonSize, glyphPlus: true,
                          onClick: () => ZoomBy(1f / 1.25f));
            AddZoomButton(offsetAngleDeg: -30f, buttonSize, glyphPlus: false,
                          onClick: () => ZoomBy(1.25f));

            // Compass rose star inside the disc (white)
            var roseGO = new GameObject("Rose");
            var roseRT = roseGO.AddComponent<RectTransform>();
            roseRT.SetParent(compassRT, false);
            roseRT.anchorMin = new Vector2(0.5f, 0.5f);
            roseRT.anchorMax = new Vector2(0.5f, 0.5f);
            roseRT.pivot = new Vector2(0.5f, 0.5f);
            roseRT.anchoredPosition = Vector2.zero;
            roseRT.sizeDelta = new Vector2(compassSize * 0.75f, compassSize * 0.75f);
            var roseImg = roseGO.AddComponent<Image>();
            roseImg.sprite = _compassRoseSprite;
            roseImg.color = new Color(0.94f, 0.91f, 0.85f, 1f);
            roseImg.raycastTarget = false;

            // Layout diagnostic
            StartCoroutine(LogRectNextFrame(mapRT));
        }

        // ---------- Sprite resolution ----------

        private static Sprite ResolveOrGenerateCircleSprite()
        {
            // Prefer the game's shipped mask sprite so we visually match CompassPro's frame.
            foreach (var s in Resources.FindObjectsOfTypeAll<Sprite>())
                if (s != null && s.name == "MiniMapMaskSolidCircle") return s;

            // Fallback: procedurally generate a circular alpha mask WITH 1.5px alpha feather so
            // the chrome frame (which uses this sprite) has a soft edge instead of stair-stepping.
            // Note: Unity's Mask stencil clip is still binary, so the feather only softens the
            // frame chrome's outer boundary — not the map-clip edge. The mask-edge stairstep is
            // covered separately by the chrome ring overlapping the mask boundary by ~1px.
            const int size = 512;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var center = size * 0.5f;
            var radius = size * 0.5f - 1f;
            const float feather = 1.5f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var dx = x + 0.5f - center;
                    var dy = y + 0.5f - center;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    var alpha = Mathf.Clamp01((radius - d) / feather + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private static Sprite ResolveArrowSprite()
        {
            // Preferred: game's shipped arrow sprite from the CompassPro asset.
            var s = FindSpriteByName("icon-arrow-white");
            if (s != null) return s;
            // Fallback: generate a stylized arrow procedurally.
            return GenerateArrowSprite();
        }

        /// <summary>
        /// Scan for a Sprite by name across three sources — loaded Sprite assets,
        /// Sprites referenced by any UI Image, and Sprites referenced by any SpriteRenderer.
        /// The first hit wins.
        /// </summary>
        private static Sprite FindSpriteByName(string name)
        {
            foreach (var sp in Resources.FindObjectsOfTypeAll<Sprite>())
                if (sp != null && sp.name == name) return sp;
            foreach (var img in Resources.FindObjectsOfTypeAll<Image>())
                if (img != null && img.sprite != null && img.sprite.name == name) return img.sprite;
            foreach (var sr in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
                if (sr != null && sr.sprite != null && sr.sprite.name == name) return sr.sprite;
            return null;
        }

        // ---------- Procedural sprite generators for the styled frame + rose + kite arrow ----------

        /// <summary>
        /// Diamond shape with optional aspect ratio. widthRatio = 1 gives a square diamond;
        /// widthRatio &lt; 1 gives a tall diamond (cardinal spec: 12w × 14h → ratio 6/7 ≈ 0.857).
        /// </summary>
        private static Sprite MakeDiamondSprite(float widthRatio = 1f)
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var cx = size * 0.5f;
            var halfH = (size - 2) * 0.5f;
            var halfW = halfH * widthRatio;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var dx = Mathf.Abs(x + 0.5f - cx) / halfW;
                    var dy = Mathf.Abs(y + 0.5f - cx) / halfH;
                    tex.SetPixel(x, y, (dx + dy) <= 1f ? Color.white : new Color(0, 0, 0, 0));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        /// <summary>
        /// Wider ring sprite for zoom-button badges (~9% of diameter). Same AA feather as the
        /// standard ring so edges stay crisp at any render size.
        /// </summary>
        private static Sprite MakeWideRingSprite()
        {
            const int size = 256;
            const float thickness = 22f;   // vs. MakeRingSprite's 10 → ~2x wider ring
            const float feather = 1f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var c = size * 0.5f;
            var outerR = c - 1f;
            var innerR = outerR - thickness;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var dx = x + 0.5f - c;
                    var dy = y + 0.5f - c;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    var outerAlpha = Mathf.Clamp01((outerR - d) / feather + 0.5f);
                    var innerAlpha = Mathf.Clamp01((d - innerR) / feather + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, Mathf.Min(outerAlpha, innerAlpha)));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        /// <summary>
        /// Thin ring sprite (annulus) for cream highlight rims + compass badge bevel, with a
        /// 1px alpha ramp on inner+outer edges so hairlines don't stair-step against the chrome.
        /// </summary>
        private static Sprite MakeRingSprite()
        {
            const int size = 256;
            const float thickness = 10f;
            const float feather = 1f;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var c = size * 0.5f;
            var outerR = c - 1f;
            var innerR = outerR - thickness;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var dx = x + 0.5f - c;
                    var dy = y + 0.5f - c;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    var outerAlpha = Mathf.Clamp01((outerR - d) / feather + 0.5f);
                    var innerAlpha = Mathf.Clamp01((d - innerR) / feather + 0.5f);
                    var alpha = Mathf.Min(outerAlpha, innerAlpha);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, alpha));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        /// <summary>4-point compass-rose star: N tallest, S/E/W shorter, waisted between points.</summary>
        private static Sprite MakeCompassRoseSprite()
        {
            const int size = 96;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var clear = new Color(0, 0, 0, 0);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, clear);

            var c = new Vector2(size * 0.5f, size * 0.5f);
            const float s = 2.4f;   // scale (mockup uses ratios ~17, ~12, ~13, ~3.5)
            // 8 outer vertices: N top, then clockwise
            var pts = new[]
            {
                new Vector2(0,           17 * s),   // N (up = +Y in texture)
                new Vector2( 3.5f * s,    3.5f * s),
                new Vector2(12   * s,     0),        // E
                new Vector2( 3.5f * s,   -3.5f * s),
                new Vector2(0,          -13 * s),    // S
                new Vector2(-3.5f * s,  -3.5f * s),
                new Vector2(-12  * s,     0),        // W
                new Vector2(-3.5f * s,    3.5f * s),
            };
            for (int i = 0; i < 8; i++) pts[i] = new Vector2(pts[i].x + c.x, pts[i].y + c.y);

            for (int i = 0; i < 8; i++)
                RasterizeTriangle(tex, c, pts[i], pts[(i + 1) % 8], Color.white);

            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        /// <summary>
        /// Kite/chevron player arrow with baked-in white outline + 55% black fill.
        /// Tightly cropped to a 52×72 sprite matching the SVG player-arrow proportions (26×36 × 2).
        /// Points UP natively. Vertices per player-arrow.svg: M12.5 1.5 L24 34 L12.5 27 L1 34 Z.
        /// </summary>
        private static Sprite MakeKitePlayerArrowSprite()
        {
            const int w = 52;
            const int h = 72;
            const int outlinePx = 3;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var clear = new Color(0, 0, 0, 0);
            var white = Color.white;
            var fill = new Color(0f, 0f, 0f, 0.55f);
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    tex.SetPixel(x, y, clear);

            // SVG vertices at 2× scale, Y flipped (SVG y-down → tex y-up).
            // SVG viewBox is 26×36; scaled to 52×72. SVG y flipped as (h - y*2).
            var top   = new Vector2(25f, 72f - 3f);    // apex near top    (SVG 12.5, 1.5)
            var right = new Vector2(48f, 72f - 68f);   // right wing        (SVG 24, 34)
            var notch = new Vector2(25f, 72f - 54f);   // notched bottom-ctr (SVG 12.5, 27)
            var left  = new Vector2( 2f, 72f - 68f);   // left wing         (SVG 1, 34)

            // Rasterize as opaque white silhouette
            RasterizeTriangle(tex, top, right, notch, white);
            RasterizeTriangle(tex, top, notch, left, white);

            // Erode by outlinePx to identify the interior. Interior pixels get semi-black fill;
            // border pixels stay opaque white → visual outline (matches SVG stroke behavior).
            var isWhite = new bool[w, h];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                    isWhite[x, y] = tex.GetPixel(x, y).a > 0.5f;

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    if (!isWhite[x, y]) continue;
                    bool interior = true;
                    for (int dy = -outlinePx; dy <= outlinePx && interior; dy++)
                    {
                        for (int dx = -outlinePx; dx <= outlinePx && interior; dx++)
                        {
                            if (dx == 0 && dy == 0) continue;
                            var nx = x + dx; var ny = y + dy;
                            if (nx < 0 || ny < 0 || nx >= w || ny >= h) { interior = false; break; }
                            if (!isWhite[nx, ny]) { interior = false; break; }
                        }
                    }
                    if (interior) tex.SetPixel(x, y, fill);
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, w, h), new Vector2(0.5f, 0.5f));
        }

        // ---------- Triangle rasterization helpers ----------

        private static void RasterizeTriangle(Texture2D tex, Vector2 a, Vector2 b, Vector2 c, Color color)
        {
            int w = tex.width;
            int h = tex.height;
            int minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, Mathf.Min(b.x, c.x))));
            int maxX = Mathf.Min(w - 1, Mathf.CeilToInt(Mathf.Max(a.x, Mathf.Max(b.x, c.x))));
            int minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, Mathf.Min(b.y, c.y))));
            int maxY = Mathf.Min(h - 1, Mathf.CeilToInt(Mathf.Max(a.y, Mathf.Max(b.y, c.y))));
            for (int y = minY; y <= maxY; y++)
                for (int x = minX; x <= maxX; x++)
                    if (PointInTriangle(new Vector2(x + 0.5f, y + 0.5f), a, b, c))
                        tex.SetPixel(x, y, color);
        }

        private static bool PointInTriangle(Vector2 p, Vector2 a, Vector2 b, Vector2 c)
        {
            var d1 = TriangleSign(p, a, b);
            var d2 = TriangleSign(p, b, c);
            var d3 = TriangleSign(p, c, a);
            var hasNeg = (d1 < 0) || (d2 < 0) || (d3 < 0);
            var hasPos = (d1 > 0) || (d2 > 0) || (d3 > 0);
            return !(hasNeg && hasPos);
        }
        private static float TriangleSign(Vector2 p1, Vector2 p2, Vector2 p3) =>
            (p1.x - p3.x) * (p2.y - p3.y) - (p2.x - p3.x) * (p1.y - p3.y);

        // ---------- Frame UI helpers ----------

        /// <summary>Adds a thin cream ring at a given diameter, layered onto the frame area.</summary>
        private void AddHighlightRing(float diameter, Color color)
        {
            var go = new GameObject("HighlightRing");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_rootRT, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(diameter, diameter);
            rt.anchoredPosition = Vector2.zero;
            var img = go.AddComponent<Image>();
            img.sprite = _ringSprite ?? (_ringSprite = MakeRingSprite());
            img.color = color;
            img.raycastTarget = false;
        }

        /// <summary>Adds a diamond marker on the frame ring. Cardinals are tall (12×14), ordinals square (7×7).</summary>
        private void AddFrameMarker(float radius, float angleDeg, float w, float h, Color color, bool isCardinal)
        {
            var go = new GameObject($"Marker_{angleDeg:0}");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_rootRT, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(w, h);
            var rad = angleDeg * Mathf.Deg2Rad;
            rt.anchoredPosition = new Vector2(Mathf.Cos(rad) * radius, Mathf.Sin(rad) * radius);
            // Rotate cardinal diamonds so their long axis is RADIAL (points outward from center).
            // For a diamond with its tall axis on the sprite's Y-axis, radial alignment means
            // rotate so sprite-up points from center to marker → localRotation.z = angleDeg - 90.
            if (isCardinal) rt.localRotation = Quaternion.Euler(0, 0, angleDeg - 90f);
            var img = go.AddComponent<Image>();
            img.sprite = isCardinal
                ? (_cardinalDiamondSprite ?? (_cardinalDiamondSprite = MakeDiamondSprite(12f/14f)))
                : (_ordinalDiamondSprite  ?? (_ordinalDiamondSprite  = MakeDiamondSprite(1f)));
            img.color = color;
            img.raycastTarget = false;
        }

        /// <summary>
        /// Adds a zoom-button badge on the frame at the given angle (measured from map center,
        /// 0° = right, CCW positive). Same disc + rim styling as the compass badge, with a
        /// plus or minus glyph inside. Button click invokes the given callback.
        /// Position: mask edge, half-overlapping the chrome band (like the compass).
        /// </summary>
        private void AddZoomButton(float offsetAngleDeg, float buttonSize, bool glyphPlus, System.Action onClick)
        {
            var size = Plugin.Size.Value;
            // Match compass placement: center sits `buttonSize/3` outside the mask edge, so ~1/6
            // of the badge overlaps into the map and ~5/6 sits on the chrome band.
            var placeRadius = size * 0.5f + buttonSize / 3f;
            var rad = offsetAngleDeg * Mathf.Deg2Rad;
            var pos = new Vector2(Mathf.Cos(rad) * placeRadius, Mathf.Sin(rad) * placeRadius);

            var go = new GameObject($"ZoomBtn_{(glyphPlus?"+":"-")}");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(_rootRT, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(buttonSize, buttonSize);
            rt.anchoredPosition = pos;

            // Dark disc background — darker than the compass at 80% opacity so the glyph reads
            // sharply against the busy chrome band underneath.
            var discImg = go.AddComponent<Image>();
            discImg.sprite = _circleMaskSprite;
            discImg.color = new Color(0f, 0f, 0f, 0.8f);
            discImg.raycastTarget = true;   // this one takes clicks

            // Cream rim — wider + more opaque than the compass badge so the button reads as
            // a physical control, not decoration.
            var rimGO = new GameObject("Rim");
            var rimRT = rimGO.AddComponent<RectTransform>();
            rimRT.SetParent(rt, false);
            rimRT.anchorMin = Vector2.zero; rimRT.anchorMax = Vector2.one; rimRT.sizeDelta = Vector2.zero;
            var rimImg = rimGO.AddComponent<Image>();
            rimImg.sprite = MakeWideRingSprite();
            rimImg.color = new Color(0.90f, 0.86f, 0.78f, 0.90f);   // cream at 90%
            rimImg.raycastTarget = false;

            // Glyph as a child
            var glyphGO = new GameObject("Glyph");
            var glyphRT = glyphGO.AddComponent<RectTransform>();
            glyphRT.SetParent(rt, false);
            glyphRT.anchorMin = new Vector2(0.5f, 0.5f); glyphRT.anchorMax = new Vector2(0.5f, 0.5f);
            glyphRT.pivot = new Vector2(0.5f, 0.5f);
            glyphRT.sizeDelta = new Vector2(buttonSize * 0.55f, buttonSize * 0.55f);
            var glyphImg = glyphGO.AddComponent<Image>();
            glyphImg.sprite = glyphPlus ? MakePlusGlyphSprite() : MakeMinusGlyphSprite();
            glyphImg.color = new Color(0.94f, 0.91f, 0.85f, 1f);   // #efe9da
            glyphImg.raycastTarget = false;

            // Click handler — use a Button component on the badge itself so any pointer inside
            // the disc counts. Button relies on the Image's raycastTarget above.
            var btn = go.AddComponent<Button>();
            btn.targetGraphic = discImg;
            btn.onClick.AddListener(() => onClick?.Invoke());
            var colors = btn.colors;
            colors.normalColor      = Color.white;
            colors.highlightedColor = new Color(1.1f, 1.1f, 1.1f);
            colors.pressedColor     = new Color(0.75f, 0.75f, 0.75f);
            btn.colors = colors;
        }

        private static void ZoomBy(float factor)
        {
            var v = Plugin.ZoomCaptureSize.Value * factor;
            Plugin.ZoomCaptureSize.Value = Mathf.Clamp(v, 50f, 1000f);
        }

        /// <summary>Cream plus sign — matches overlay-glyph-plus.svg (two 2px-stroke lines).</summary>
        private static Sprite MakePlusGlyphSprite() => MakeGlyphSprite(plus: true);
        /// <summary>Cream minus sign — matches overlay-glyph-minus.svg (single 2px-stroke line).</summary>
        private static Sprite MakeMinusGlyphSprite() => MakeGlyphSprite(plus: false);

        private static Sprite MakeGlyphSprite(bool plus)
        {
            const int size = 64;
            const int strokeHalf = 3;              // ~6px stroke on a 64px sprite (scaled down to 2px on screen)
            const int inset = 12;                  // matches SVG line endpoints at x=10.5 to x=24.5 (of 35)
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var clear = new Color(0, 0, 0, 0);
            var white = Color.white;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, clear);

            var cy = size / 2;
            var cx = size / 2;
            // Horizontal bar
            for (int x = inset; x < size - inset; x++)
                for (int dy = -strokeHalf; dy <= strokeHalf; dy++)
                    tex.SetPixel(x, cy + dy, white);
            if (plus)
            {
                // Vertical bar
                for (int y = inset; y < size - inset; y++)
                    for (int dx = -strokeHalf; dx <= strokeHalf; dx++)
                        tex.SetPixel(cx + dx, y, white);
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        /// <summary>Adds one intercardinal ray on the compass badge — a thin cream line at the given angle.</summary>
        private void AddCompassRay(RectTransform parent, float compassSize, float angleDeg)
        {
            // Star arms reach ~40% of compass half-radius; rim starts at ~90%. Put rays in
            // that gap so they read as "under-the-star" tick marks rather than being
            // occluded by the star silhouette.
            var inner = compassSize * 0.5f * 0.55f;
            var outer = compassSize * 0.5f * 0.88f;
            var rad = angleDeg * Mathf.Deg2Rad;
            var midR = (inner + outer) * 0.5f;
            var length = outer - inner;
            var go = new GameObject($"Ray_{angleDeg:0}");
            var rt = go.AddComponent<RectTransform>();
            rt.SetParent(parent, false);
            rt.anchorMin = new Vector2(0.5f, 0.5f);
            rt.anchorMax = new Vector2(0.5f, 0.5f);
            rt.pivot = new Vector2(0.5f, 0.5f);
            rt.sizeDelta = new Vector2(length, 1.4f);
            rt.anchoredPosition = new Vector2(Mathf.Cos(rad) * midR, Mathf.Sin(rad) * midR);
            rt.localRotation = Quaternion.Euler(0, 0, angleDeg);
            var img = go.AddComponent<Image>();
            img.color = new Color(0.94f, 0.91f, 0.85f, 0.65f);   // #efe9da @ 65% (bumped from 45%)
            img.raycastTarget = false;
        }

        // ---------- Scrollwork ring (procedural cubic-bezier rasterization) ----------

        /// <summary>
        /// Twelve volute pairs rendered around a ring, per bezel-scrollwork-ring.svg.
        /// Each pair is the SVG path "M138 19 C148 5 164 5 170 13 …" and its horizontal mirror,
        /// rotated at 30° intervals around center (182, 182) in the source viewBox.
        /// </summary>
        private static Sprite MakeScrollworkRingSprite()
        {
            const int size = 512;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var clear = new Color(0, 0, 0, 0);
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                    tex.SetPixel(x, y, clear);

            var scale = size / 364f;             // SVG viewBox is 364 wide
            // Style pass option B: brighter stroke + 1px drop shadow gives an engraved-metal look
            // that stands out against the dark chrome band. Shadow rendered first, highlight on top.
            var shadow    = new Color(0.10f, 0.09f, 0.08f, 0.80f);
            var highlight = new Color(0.82f, 0.72f, 0.53f, 1.00f);   // #d1b787 warm cream
            var strokeW   = 2.2f * scale;

            // Left volute path (as in SVG). Each cubic Bezier segment is (start, ctrl1, ctrl2, end).
            var left = new[]
            {
                new Vector2(138, 19), new Vector2(148,  5), new Vector2(164,  5), new Vector2(170, 13),
                new Vector2(170, 13), new Vector2(173, 18), new Vector2(169, 22), new Vector2(166, 19),
                new Vector2(166, 19), new Vector2(164, 17), new Vector2(166, 14), new Vector2(169, 15),
            };

            // Shadow pass — offset 1px in tex space toward the outer rim (away from map center).
            for (int i = 0; i < 12; i++)
            {
                var rotDeg = i * 30f;
                var rad = rotDeg * Mathf.Deg2Rad;
                // Radial-outward offset (rotates with each tile so shadow always falls "down and out").
                var offset = new Vector2(Mathf.Sin(rad) * 1.2f, -Mathf.Cos(rad) * 1.2f);
                DrawVolute(tex, size, scale, left, mirror: false, rotDeg, shadow, strokeW, offset);
                DrawVolute(tex, size, scale, left, mirror: true,  rotDeg, shadow, strokeW, offset);
            }
            // Highlight pass on top
            for (int i = 0; i < 12; i++)
            {
                var rotDeg = i * 30f;
                DrawVolute(tex, size, scale, left, mirror: false, rotDeg, highlight, strokeW, Vector2.zero);
                DrawVolute(tex, size, scale, left, mirror: true,  rotDeg, highlight, strokeW, Vector2.zero);
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private static void DrawVolute(Texture2D tex, int size, float scale, Vector2[] path,
                                       bool mirror, float rotDeg, Color color, float strokeW, Vector2 pixelOffset)
        {
            // Copy + transform path into texture space.
            var xf = new Vector2[path.Length];
            var rad = rotDeg * Mathf.Deg2Rad;
            var cos = Mathf.Cos(rad); var sin = Mathf.Sin(rad);
            for (int i = 0; i < path.Length; i++)
            {
                var p = path[i];
                if (mirror) p.x = 364f - p.x;         // horizontal mirror around SVG viewBox center
                var dx = p.x - 182f;                   // shift so rotation center is origin
                var dy = 182f - p.y;                   // SVG Y-down → texture Y-up
                var rx = dx * cos - dy * sin;
                var ry = dx * sin + dy * cos;
                xf[i] = new Vector2(size * 0.5f + rx * scale + pixelOffset.x,
                                    size * 0.5f + ry * scale + pixelOffset.y);
            }
            // 3 cubic segments: verts (0,1,2,3), (4,5,6,7), (8,9,10,11)
            for (int seg = 0; seg < 3; seg++)
                DrawCubicBezier(tex, size, xf[seg*4], xf[seg*4+1], xf[seg*4+2], xf[seg*4+3], color, strokeW);
        }

        private static void DrawCubicBezier(Texture2D tex, int size, Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, Color color, float strokeW)
        {
            const int steps = 40;
            var prev = p0;
            for (int i = 1; i <= steps; i++)
            {
                var t = i / (float)steps;
                var u = 1f - t;
                var uu = u * u;   var tt = t * t;
                var pt = uu*u*p0 + 3f*uu*t*p1 + 3f*u*tt*p2 + tt*t*p3;
                DrawLine(tex, size, prev, pt, color, strokeW);
                prev = pt;
            }
        }

        private static void DrawLine(Texture2D tex, int size, Vector2 a, Vector2 b, Color color, float thickness)
        {
            var minX = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.x, b.x) - thickness));
            var maxX = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(a.x, b.x) + thickness));
            var minY = Mathf.Max(0, Mathf.FloorToInt(Mathf.Min(a.y, b.y) - thickness));
            var maxY = Mathf.Min(size - 1, Mathf.CeilToInt(Mathf.Max(a.y, b.y) + thickness));
            var half = thickness * 0.5f;
            var ab = b - a;
            var abLen2 = ab.sqrMagnitude;
            if (abLen2 < 1e-4f) return;
            for (int y = minY; y <= maxY; y++)
            {
                for (int x = minX; x <= maxX; x++)
                {
                    var p = new Vector2(x + 0.5f, y + 0.5f);
                    var t = Mathf.Clamp01(Vector2.Dot(p - a, ab) / abLen2);
                    var proj = a + t * ab;
                    if ((p - proj).sqrMagnitude <= half * half) tex.SetPixel(x, y, color);
                }
            }
        }

        /// <summary>Procedural triangle-arrow pointing DOWN (matches icon-arrow-white orientation).</summary>
        private static Sprite GenerateArrowSprite()
        {
            const int size = 64;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var centerX = size * 0.5f;
            for (int y = 0; y < size; y++)
            {
                // Width at row y — triangle apex at Y=0 (bottom), base at Y=size-1 (top).
                // Narrow the base a bit and add stem so it reads as an arrow, not just a triangle.
                var yFrac = y / (float)(size - 1);
                var halfWidthAtY = yFrac * (size * 0.5f);
                // Optional stem: at top half, cap width
                if (yFrac > 0.55f) halfWidthAtY = Mathf.Min(halfWidthAtY, size * 0.16f);
                for (int x = 0; x < size; x++)
                {
                    var dx = Mathf.Abs(x - centerX);
                    var inside = dx <= halfWidthAtY;
                    tex.SetPixel(x, y, inside ? Color.white : new Color(0, 0, 0, 0));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        private System.Collections.IEnumerator LogRectNextFrame(RectTransform target)
        {
            yield return null;   // wait one frame for layout
            var corners = new Vector3[4];
            target.GetWorldCorners(corners);
            Plugin.Log.LogInfo($"[MinimapV2] map RectTransform world corners: BL={corners[0]}  TL={corners[1]}  TR={corners[2]}  BR={corners[3]}");
            Plugin.Log.LogInfo($"[MinimapV2] map anchoredPos={target.anchoredPosition}  sizeDelta={target.sizeDelta}  rect={target.rect}");
        }

        // ---------- Lifecycle ----------

        internal void SetVisible(bool visible)
        {
            _visible = visible;
            if (_canvasGO != null) _canvasGO.SetActive(visible);
            if (_cam != null) _cam.enabled = visible;
            Plugin.Log.LogInfo($"[MinimapV2] visibility → {visible}");
        }

        internal bool IsMinimapOn => _visible;

        /// <summary>
        /// If the minimap is visible and laid out, fills the chrome-frame rect in IMGUI
        /// screen coordinates (top-left origin, Y increasing downward). Lets IMGUI HUD
        /// elements (coord bar, biome nameplate) position themselves relative to the actual
        /// minimap regardless of anchor / size changes. Returns false when the map is hidden.
        /// </summary>
        public bool TryGetChromeScreenRect(out Rect chromeRect)
        {
            chromeRect = default;
            if (!_visible || _rootRT == null) return false;
            var corners = new Vector3[4];
            _rootRT.GetWorldCorners(corners);
            // 0=BL, 1=TL, 2=TR, 3=BR in Y-up screen space (Overlay canvas → world == screen).
            var mapLeft = corners[0].x;
            var mapRight = corners[2].x;
            var mapTop_YUp = corners[1].y;
            var mapBottom_YUp = corners[0].y;
            var mapW = mapRight - mapLeft;
            var mapH = mapTop_YUp - mapBottom_YUp;
            const float frameThickness = 20f;   // must match BuildUI
            var chromeLeft = mapLeft - frameThickness;
            var chromeTop_YDown = Screen.height - (mapTop_YUp + frameThickness);
            chromeRect = new Rect(chromeLeft, chromeTop_YDown, mapW + frameThickness * 2f, mapH + frameThickness * 2f);
            return true;
        }

        internal Vector3 CurrentFollowPosition => _followTarget != null ? _followTarget.position : Vector3.zero;

        // ---------- Per-frame ----------

        private void LateUpdate()
        {
            if (!_visible || _cam == null) return;

            // Re-resolve follow target if lost (scene reload) — retry cheaply, don't spam.
            if (_followTarget == null && Time.unscaledTime >= _nextFollowResolveTime)
            {
                _nextFollowResolveTime = Time.unscaledTime + 1f;
                _followTarget = ResolveFollowTarget();
            }
            if (_followTarget == null) return;

            // Camera geometry — locked north-up top-down.
            var zoom = Plugin.ZoomCaptureSize.Value;
            _cam.orthographicSize = zoom * 0.5f;
            _cam.transform.position = _followTarget.position + Vector3.up * Mathf.Max(200f, zoom);
            _cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);

            // Cheap: keep culling mask synced to config toggles each frame.
            _cam.cullingMask = ComputeCullingMask();

            // Player arrow — rotate to indicate the camera's cardinal facing.
            // Kite sprite points UP natively; Unity UI rotation is counter-clockwise-positive,
            // world yaw increases clockwise from north (+Z), so rotation = -yaw makes the arrow's
            // tip track the camera's forward direction on a north-up map.
            if (_playerArrowRT != null)
            {
                if (_cachedMainCam == null) _cachedMainCam = ResolveMainCamera();
                var yaw = _cachedMainCam != null ? HorizontalYawFrom(_cachedMainCam.transform)
                                                 : HorizontalYawFrom(_followTarget);
                _playerArrowRT.localRotation = Quaternion.Euler(0f, 0f, -yaw);
            }

            // POI overlay — rescan periodically, reproject every frame.
            UpdatePoiOverlay(zoom);

            // Diagnostic heartbeat.
            if (Time.unscaledTime >= _nextDiagLog)
            {
                _nextDiagLog = Time.unscaledTime + 5f;
                Plugin.Log.LogInfo($"[MinimapV2] tick  follow={_followTarget.position}  zoom={zoom:0}  cull=0x{_cam.cullingMask:X8}  pois={_pois.Count}");
            }
        }

        // ---------- POI overlay ----------

        /// <summary>
        /// Scans CompassProPOI instances every few seconds and keeps a matching Image on the
        /// mask overlay. Positions are reprojected each frame from world space into map pixels
        /// relative to the follow target. POIs outside the circle are edge-clamped so the player
        /// still gets a direction hint at the border.
        /// </summary>
        private void UpdatePoiOverlay(float zoomWorldMeters)
        {
            if (_poiRootRT == null || _followTarget == null) return;
            if (Time.unscaledTime >= _nextPoiScan)
            {
                _nextPoiScan = Time.unscaledTime + 3f;
                RescanPois();
            }

            var mapPixels = Plugin.Size.Value;
            var pxPerMeter = mapPixels / Mathf.Max(1f, zoomWorldMeters);
            var radiusPx = mapPixels * 0.5f;
            var playerPos = _followTarget.position;

            for (int i = _pois.Count - 1; i >= 0; i--)
            {
                var p = _pois[i];
                if (p.Src == null || p.Rt == null)
                {
                    if (p.Rt != null) Destroy(p.Rt.gameObject);
                    _pois.RemoveAt(i);
                    continue;
                }

                // Visibility: skip AlwaysHidden; everything else is gated purely by whether
                // it falls inside the current zoom radius. We DON'T honor visibleDistanceOverride
                // for the minimap because that field is authored for the compass-bar reveal
                // radius, which is typically much smaller than the minimap capture radius (so
                // POIs authored as "compass-bar-only when close" would never appear on our map).
                if (p.Src.miniMapVisibility == global::CompassNavigatorPro.POI_VISIBILITY.AlwaysHidden)
                {
                    p.Rt.gameObject.SetActive(false);
                    continue;
                }

                var wp = p.Src.transform.position;
                var dx = wp.x - playerPos.x;
                var dz = wp.z - playerPos.z;

                // World X = map X (east+), World Z = map Y (north+); we're north-up locked.
                var offset = new Vector2(dx * pxPerMeter, dz * pxPerMeter);
                var mag = offset.magnitude;
                // Off-map POIs are hidden — no edge-clamp indicator (the "bedroll pointer"
                // read as visual noise on the minimap even though it's a real off-map hint).
                if (mag > radiusPx)
                {
                    p.Rt.gameObject.SetActive(false);
                    continue;
                }
                p.Rt.gameObject.SetActive(true);
                p.Rt.anchoredPosition = offset;

                // Live-refresh sprite + tint so visited-state changes propagate mid-session
                // without waiting for the next 3s rescan (fixes iconography-vs-state drift).
                var desired = (p.Src.isVisited && p.Src.iconVisited != null) ? p.Src.iconVisited : p.Src.iconNonVisited;
                if (desired != null && p.Img.sprite != desired) p.Img.sprite = desired;
                var wantColor = p.Src.tintColor.a > 0.05f ? p.Src.tintColor : new Color(1f, 0.75f, 0.25f, 1f);
                if (p.Img.color != wantColor) p.Img.color = wantColor;
            }
        }

        private void RescanPois()
        {
            // includeInactive: true — CompassPro/World_Map_Mgr appears to leave undiscovered POI
            // GameObjects inactive until the world map is opened. We want them anyway.
            var found = Object.FindObjectsOfType<global::CompassNavigatorPro.CompassProPOI>(includeInactive: true);
            var prevCount = _pois.Count;

            // Cleanup pass: drop any already-tracked POI that turns out to be player-owned
            // (e.g., the player-position POI CompassPro attaches to the player root — we render
            // our own kite arrow for that, and don't want it duplicated).
            for (int i = _pois.Count - 1; i >= 0; i--)
            {
                if (_pois[i].Src == null || IsPlayerOwnedPoi(_pois[i].Src))
                {
                    if (_pois[i].Rt != null) Destroy(_pois[i].Rt.gameObject);
                    _pois.RemoveAt(i);
                }
            }

            // Add newcomers
            foreach (var src in found)
            {
                if (src == null) continue;
                if (IsPlayerOwnedPoi(src)) continue;
                bool already = false;
                for (int i = 0; i < _pois.Count; i++) if (_pois[i].Src == src) { already = true; break; }
                if (already) continue;

                var go = new GameObject($"POI_{(string.IsNullOrEmpty(src.title) ? src.name : src.title)}");
                var rt = go.AddComponent<RectTransform>();
                rt.SetParent(_poiRootRT, false);
                rt.anchorMin = new Vector2(0.5f, 0.5f);
                rt.anchorMax = new Vector2(0.5f, 0.5f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                // Base icon size doubled from 12 → 24; still scales with the POI's own
                // miniMapIconScale so authored icons keep their relative sizing.
                var baseSize = 24f * Mathf.Max(0.5f, src.miniMapIconScale > 0 ? src.miniMapIconScale : 1f);
                rt.sizeDelta = new Vector2(baseSize, baseSize);

                var img = go.AddComponent<Image>();
                var srcSprite = (src.isVisited && src.iconVisited != null) ? src.iconVisited : src.iconNonVisited;
                img.sprite = srcSprite != null ? srcSprite : _poiFallbackSprite;
                img.color = src.tintColor.a > 0.05f ? src.tintColor : new Color(1f, 0.75f, 0.25f, 1f);
                img.preserveAspect = true;
                img.raycastTarget = false;

                _pois.Add(new PoiMarker { Src = src, Rt = rt, Img = img });
                Plugin.Log.LogInfo($"[MinimapV2] POI +add  title='{src.title}'  name='{src.name}'  pos={src.transform.position}  minimapVis={src.miniMapVisibility}  scale={src.miniMapIconScale}");
            }
            if (_pois.Count != prevCount)
                Plugin.Log.LogInfo($"[MinimapV2] POI rescan  found={found.Length}  tracked={_pois.Count}  delta={_pois.Count-prevCount}");
        }

        /// <summary>
        /// True if this POI represents the player itself (attached to the player transform
        /// hierarchy, or titled as such). We render our own kite arrow for the player, so this
        /// filters out CompassPro's redundant player-position POI marker.
        /// </summary>
        private bool IsPlayerOwnedPoi(global::CompassNavigatorPro.CompassProPOI poi)
        {
            if (poi == null) return false;
            // Descendant check: POI transform is on or under the follow target.
            if (_followTarget != null)
            {
                var t = poi.transform;
                while (t != null)
                {
                    if (t == _followTarget) return true;
                    t = t.parent;
                }
            }
            // Title heuristic backup — covers cases where the POI lives under a sibling GO
            // with a "player" name but isn't parented to the actual player transform.
            if (!string.IsNullOrEmpty(poi.title)
                && poi.title.IndexOf("player", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            if (!string.IsNullOrEmpty(poi.name)
                && poi.name.IndexOf("player", System.StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
            return false;
        }

        /// <summary>Small filled circle sprite used as a fallback when a POI has no icon.</summary>
        private static Sprite MakePoiDotSprite()
        {
            const int size = 32;
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false);
            tex.hideFlags = HideFlags.HideAndDontSave;
            tex.filterMode = FilterMode.Bilinear;
            var c = size * 0.5f;
            var r = size * 0.5f - 1f;
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    var dx = x + 0.5f - c;
                    var dy = y + 0.5f - c;
                    var d = Mathf.Sqrt(dx * dx + dy * dy);
                    var a = Mathf.Clamp01(r - d + 0.5f);
                    tex.SetPixel(x, y, new Color(1f, 1f, 1f, a));
                }
            }
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f));
        }

        // ---------- Helpers ----------

        /// <summary>Yaw of the transform's forward direction projected onto the horizontal plane.</summary>
        private static float HorizontalYawFrom(Transform t)
        {
            if (t == null) return 0f;
            var f = t.forward;
            f.y = 0f;
            if (f.sqrMagnitude < 1e-6f) return t.eulerAngles.y;
            f.Normalize();
            return Mathf.Atan2(f.x, f.z) * Mathf.Rad2Deg;
        }

        private static Camera ResolveMainCamera()
        {
            var cc = MgrHub.Cam;
            if (cc != null && cc._mainCam != null) return cc._mainCam;
            foreach (var c in Object.FindObjectsOfType<Camera>())
                if (c != null && c.gameObject.name == "Main_Camera") return c;
            return Camera.main;
        }

        private static Transform ResolveFollowTarget()
        {
            var players = Object.FindObjectsOfType<global::Player_Input>(includeInactive: false);
            if (players != null && players.Length > 0) return players[0].transform;
            var cam = ResolveMainCamera();
            return cam != null ? cam.transform : null;
        }
    }
}
