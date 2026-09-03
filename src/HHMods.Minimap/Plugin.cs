using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using HHMods.Core;
using UnityEngine;

namespace HHMods.Minimap
{
    [BepInPlugin(PluginId, "HH Minimap", "0.1.0")]
    [BepInDependency(HHMods.Core.Plugin.PluginId)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginId = "io.hh.minimap";

        internal static Plugin Instance { get; private set; }
        internal static ManualLogSource Log { get; private set; }

        // ---- Config: hotkeys + display ----
        internal static ConfigEntry<KeyboardShortcut> ToggleKey;
        internal static ConfigEntry<KeyboardShortcut> TuningPanelKey;
        internal static ConfigEntry<bool> ShowOnStart;
        internal static ConfigEntry<bool> UseNewMinimap;
        internal static ConfigEntry<float> VisibleRadius;
        internal static ConfigEntry<float> Size;
        internal static ConfigEntry<Vector2> ScreenAnchor01;
        internal static ConfigEntry<bool> DisableFogOfWar;
        internal static ConfigEntry<float> ZoomCaptureSize;

        // ---- Config: rendering (live-tunable) ----
        internal static ConfigEntry<bool> UseFixedExposure;
        internal static ConfigEntry<float> FixedExposureEV;
        internal static ConfigEntry<float> LightIntensityLux;
        internal static ConfigEntry<float> LightAngleX;
        internal static ConfigEntry<float> LightAngleY;
        internal static ConfigEntry<float> Saturation;

        // ---- Config: arrow (live-tunable) ----
        internal static ConfigEntry<float> ArrowSize;
        internal static ConfigEntry<float> ArrowFillAlpha;
        internal static ConfigEntry<float> ArrowOutlineDistance;
        internal static ConfigEntry<float> ArrowOutlineAlpha;

        // ---- Config: camera culling (live-tunable) ----
        internal static ConfigEntry<bool> CullTransparentFX;
        internal static ConfigEntry<bool> CullWater;
        internal static ConfigEntry<bool> CullUI;

        private Harmony _harmony;

        private void Awake()
        {
            Instance = this;
            Log = Logger;

            // --- Hotkeys ---
            ToggleKey = Config.Bind("Hotkeys", "Toggle Minimap",
                new KeyboardShortcut(KeyCode.N),
                "Show / hide the minimap. Default N — avoid Shift+M which the game uses for the world map.");
            TuningPanelKey = Config.Bind("Hotkeys", "Toggle Tuning Panel",
                new KeyboardShortcut(KeyCode.F9),
                "Show / hide the live-tuning HUD panel (sliders for light, exposure, saturation, arrow style).");

            // --- Display ---
            ShowOnStart = Config.Bind("Display", "Show On Start", false,
                "If true, the minimap and filter panel are visible as soon as the game world loads.");
            UseNewMinimap = Config.Bind("Display", "Use New Minimap (V2)", true,
                "If true, use the from-scratch V2 minimap (own camera + RT + canvas — no CompassPro). "
              + "If false, use the original CompassPro-clone V1 implementation. "
              + "Restart the game (or reload the scene) after changing.");
            VisibleRadius = Config.Bind("Display", "Visible Radius", 150f,
                "World-space radius of POIs shown on the minimap (in meters).");
            Size = Config.Bind("Display", "Panel Size", 260f,
                "Minimap panel size in screen pixels.");
            ScreenAnchor01 = Config.Bind("Display", "Screen Anchor (0-1)", new Vector2(0.98f, 0.98f),
                "Anchor fraction — (1,1) = top-right, (0,0) = bottom-left.");
            DisableFogOfWar = Config.Bind("Debug", "Disable Fog Of War", true,
                "If true, attempt to disable CompassPro's fog-of-war overlays that may darken the minimap.");
            ZoomCaptureSize = Config.Bind("Display", "Zoom Capture Size (m)", 260f,
                new ConfigDescription("World-space horizontal radius the minimap camera captures. "
                                    + "Lower = zoomed in, higher = zoomed out. Adjustable in-game with the +/- buttons on the HUD bar.",
                    new AcceptableValueRange<float>(50f, 1000f)));

            // --- Rendering (live-tunable) ---
            UseFixedExposure = Config.Bind("Rendering", "Use Fixed Exposure", true,
                "If true, the minimap uses an isolated Volume with a fixed-EV Exposure so brightness is "
              + "independent of the game's day/night cycle. If false, the minimap inherits the scene exposure.");
            FixedExposureEV = Config.Bind("Rendering", "Fixed Exposure EV", 10f,
                new ConfigDescription("Exposure value in physical camera stops. ~12 = outdoor daylight, ~10 = mid, "
                                    + "~9 = overcast, ~6 = deep shade. Higher = brighter.",
                    new AcceptableValueRange<float>(0f, 22f)));
            LightIntensityLux = Config.Bind("Rendering", "Light Intensity (lux)", 40000f,
                new ConfigDescription("Directional light intensity in lux, applied only during the minimap's render pass. "
                                    + "~40k = overcast day, ~100k = direct sunlight. Too high blows highlights; too low leaves night dark.",
                    new AcceptableValueRange<float>(0f, 200000f)));
            LightAngleX = Config.Bind("Rendering", "Light Pitch (X°)", 60f,
                new ConfigDescription("Downward tilt of the minimap directional light. 90 = straight down, 0 = horizontal.",
                    new AcceptableValueRange<float>(0f, 90f)));
            LightAngleY = Config.Bind("Rendering", "Light Yaw (Y°)", 30f,
                new ConfigDescription("Horizontal rotation of the minimap directional light. Affects shading direction.",
                    new AcceptableValueRange<float>(0f, 360f)));
            Saturation = Config.Bind("Rendering", "Saturation", -30f,
                new ConfigDescription("Color saturation adjustment applied only to the minimap render. "
                                    + "0 = normal, -100 = grayscale, +100 = vivid.",
                    new AcceptableValueRange<float>(-100f, 100f)));

            // --- Arrow (live-tunable) ---
            ArrowSize = Config.Bind("Arrow", "Size", 18f,
                new ConfigDescription("Player arrow size in UI pixels.",
                    new AcceptableValueRange<float>(4f, 40f)));
            ArrowFillAlpha = Config.Bind("Arrow", "Fill Alpha", 0.85f,
                new ConfigDescription("Opacity of the black fill (0 = invisible, 1 = fully opaque). "
                                    + "Higher = better contrast on bright maps, less shadow needed.",
                    new AcceptableValueRange<float>(0f, 1f)));
            ArrowOutlineDistance = Config.Bind("Arrow", "Outline Distance (px)", 1.0f,
                new ConfigDescription("Drop-shadow offset for the arrow outline. 0 = no outline. "
                                    + "Values above ~1.5 start reading as a second arrow rather than a subtle shadow.",
                    new AcceptableValueRange<float>(0f, 5f)));
            ArrowOutlineAlpha = Config.Bind("Arrow", "Outline Alpha", 0.6f,
                new ConfigDescription("Opacity of the drop-shadow. 0 = invisible.",
                    new AcceptableValueRange<float>(0f, 1f)));

            // --- Camera culling (live-tunable) ---
            CullTransparentFX = Config.Bind("Camera Culling", "Cull TransparentFX Layer (1)", true,
                "Excludes Unity's TransparentFX layer from the minimap render. Usually safe to leave enabled.");
            CullWater = Config.Bind("Camera Culling", "Cull Water Layer (4)", true,
                "Excludes the Water layer from the minimap. Water rendering top-down is often noisy.");
            CullUI = Config.Bind("Camera Culling", "Cull UI Layer (5)", true,
                "Excludes the UI layer. Fixes CompassPro's world-space compass-bar icons rendering huge on the minimap.");

            // --- Live-apply wiring: every tunable ConfigEntry re-applies its value to the running minimap on change ---
            LightIntensityLux.SettingChanged    += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyLightIntensityLive());
            LightAngleX.SettingChanged          += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyLightAngleLive());
            LightAngleY.SettingChanged          += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyLightAngleLive());
            FixedExposureEV.SettingChanged      += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyExposureLive());
            Saturation.SettingChanged           += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplySaturationLive());
            ArrowSize.SettingChanged            += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyArrowStyleLive());
            ArrowFillAlpha.SettingChanged       += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyArrowStyleLive());
            ArrowOutlineDistance.SettingChanged += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyArrowStyleLive());
            ArrowOutlineAlpha.SettingChanged    += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyArrowStyleLive());
            CullTransparentFX.SettingChanged    += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyCullMaskLive());
            CullWater.SettingChanged            += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyCullMaskLive());
            CullUI.SettingChanged               += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyCullMaskLive());
            ZoomCaptureSize.SettingChanged      += (_, __) => Scheduler.Post(() => MinimapController.Instance?.ApplyZoomLive());

            // Tuning panel + HUD bar — attach to our own persistent GameObject so they
            // work even before HubReady.
            gameObject.AddComponent<TuningPanel>();
            gameObject.AddComponent<MinimapHUDBar>();

            _harmony = new Harmony(PluginId);
            try
            {
                _harmony.PatchAll(typeof(Plugin).Assembly);
                Log.LogInfo($"[Minimap] Harmony patched — {_harmony.GetPatchedMethods()?.ToString()}");
            }
            catch (System.Exception e)
            {
                Log.LogError($"[Minimap] Harmony patch failed: {e}");
            }

            GameEvents.HubReady += OnHubReady;

            Log.LogInfo($"{PluginId} loaded — awaiting HubReady");
        }

        private void OnHubReady()
        {
            var host = new GameObject("HHMods.Minimap.Controller");
            Object.DontDestroyOnLoad(host);

            if (UseNewMinimap.Value)
            {
                host.AddComponent<V2.MinimapV2Controller>();
                host.AddComponent<V2.MinimapV2HotkeyBinding>();
                Log.LogInfo("[Minimap] V2 (from-scratch) controller spawned");
            }
            else
            {
                host.AddComponent<MinimapController>();
                host.AddComponent<HotkeyBinding>();
                var panel = host.AddComponent<FilterPanel>();
                panel.SetVisible(ShowOnStart.Value);
                Log.LogInfo("[Minimap] V1 (CompassPro-clone) controller spawned");
            }

            BuiltinLayers.RegisterAll();
        }
    }
}
